using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarRentalAutomation.Shared;

namespace CarRentalAutomation.Payments.Api;

internal interface ICheckoutProvider
{
    Task<CheckoutState> InitializeAsync(PaymentRow payment, BuyerDetails buyer, string ip, CancellationToken cancellationToken);
    Task<ProviderResult> RetrieveAsync(PaymentRow payment, CheckoutState checkout, CancellationToken cancellationToken);
}

internal sealed class IyzicoCheckoutProvider(HttpClient client, ServerConfiguration configuration) : ICheckoutProvider
{
    internal const string InitializePath = "/payment/iyzipos/checkoutform/initialize/auth/ecom";
    internal const string RetrievePath = "/payment/iyzipos/checkoutform/auth/ecom/detail";

    public async Task<CheckoutState> InitializeAsync(PaymentRow payment, BuyerDetails buyer, string ip, CancellationToken cancellationToken)
    {
        configuration.RequireReady();
        using var document = await PostAsync(InitializePath, CreateCheckoutRequest(payment, buyer, ip), cancellationToken);
        return ReadCheckoutState(payment, document.RootElement);
    }

    private object CreateCheckoutRequest(PaymentRow payment, BuyerDetails buyer, string ip)
    {
        var price = Money(payment.Amount);
        var address = new
        {
            address = buyer.Address,
            contactName = buyer.Name + " " + buyer.Surname,
            city = buyer.City,
            country = buyer.Country,
            zipCode = buyer.ZipCode
        };
        return new
        {
            locale = "tr",
            conversationId = payment.ConversationId,
            price,
            paidPrice = price,
            currency = payment.Currency,
            basketId = payment.BasketId,
            paymentGroup = "PRODUCT",
            callbackUrl = configuration.CallbackUrl,
            enabledInstallments = new[] { 1 },
            buyer = new
            {
                id = payment.Booking.UserId.ToString(CultureInfo.InvariantCulture),
                name = buyer.Name,
                surname = buyer.Surname,
                identityNumber = buyer.IdentityNumber,
                email = buyer.Email,
                gsmNumber = buyer.PhoneNumber,
                registrationAddress = buyer.Address,
                city = buyer.City,
                country = buyer.Country,
                zipCode = buyer.ZipCode,
                ip
            },
            billingAddress = address,
            shippingAddress = address,
            basketItems = new[] { new { id = payment.ItemId, price, name = payment.Booking.Model,
                category1 = "Car Rental Simulation", itemType = "VIRTUAL" } }
        };
    }

    private CheckoutState ReadCheckoutState(PaymentRow payment, JsonElement root)
    {
        if (Text(root, "status") == "failure")
        {
            var code = SafeError(root);
            var explanation = code switch
            {
                "3" => " Alıcı e-posta adresi gerekli.",
                "4" => " Alıcı e-posta adresi en fazla 100 karakter olmalıdır.",
                "5" => " iyzico alıcı e-posta adresinin biçimini kabul etmedi. E-posta alanını düzeltip aynı rezervasyon için tekrar deneyin.",
                _ => ""
            };
            throw new ProviderRejectedException("iyzico ödeme formunu başlatamadı. Hata kodu: " + code + "." + explanation);
        }
        var token = Text(root, "token");
        var url = Text(root, "paymentPageUrl");
        if (Text(root, "status") != "success" || Text(root, "conversationId") != payment.ConversationId ||
            !ValidToken(token) || !CheckoutUrlMatchesToken(url, token!) ||
            !VerifySignature(configuration.SecretKey!, Text(root, "signature"), payment.ConversationId, token!))
            throw new PaymentApiException(502, "iyzico form yanıtının bütünlüğü doğrulanamadı. Yeni ödeme denemeden önce mevcut denemeyi kontrol edin.");
        var lifetime = root.TryGetProperty("tokenExpireTime", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 1, 86400) : 1800;
        return new(token!, url!, DateTime.UtcNow.AddSeconds(lifetime));
    }

    public async Task<ProviderResult> RetrieveAsync(PaymentRow payment, CheckoutState checkout, CancellationToken cancellationToken)
    {
        configuration.RequireReady();
        using var document = await PostAsync(RetrievePath,
            new { locale = "tr", conversationId = payment.ConversationId, token = checkout.Token }, cancellationToken);
        return ValidateResult(payment, checkout, document.RootElement, configuration.SecretKey!);
    }

    internal static ProviderResult ValidateResult(PaymentRow payment, CheckoutState checkout, JsonElement root, string secret)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "status") != "success" ||
            Text(root, "conversationId") != payment.ConversationId || Text(root, "token") != checkout.Token ||
            Text(root, "basketId") != payment.BasketId || Text(root, "currency") != payment.Currency ||
            !Decimal(root, "price", out var price) || !Decimal(root, "paidPrice", out var paid) ||
            price != payment.Amount || paid != payment.Amount || payment.Amount != payment.Booking.Amount ||
            payment.Currency != payment.Booking.Currency)
            return new(PaymentStatus.Review);
        var reference = Text(root, "paymentId");
        var status = Text(root, "paymentStatus");
        if (!ValidReference(reference) || !VerifySignature(secret, Text(root, "signature"), status ?? "", reference!,
            payment.Currency, payment.BasketId, payment.ConversationId, Money(paid), Money(price), checkout.Token))
            return new(PaymentStatus.Review);
        if (status == "FAILURE")
            return new(checkout.ExpiresAt <= DateTime.UtcNow ? PaymentStatus.Expired : PaymentStatus.Failed, reference);
        if (status != "SUCCESS" || !Integer(root, "fraudStatus", out var fraud)) return new(PaymentStatus.Review);
        if (fraud == -1) return new(PaymentStatus.Failed, reference);
        if (fraud != 1 || !root.TryGetProperty("itemTransactions", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1) return new(PaymentStatus.Review);
        var item = items[0];
        if (Text(item, "itemId") != payment.ItemId || !Decimal(item, "price", out var itemPrice) || itemPrice != payment.Amount ||
            !Decimal(item, "paidPrice", out var itemPaid) || itemPaid != payment.Amount ||
            !Integer(item, "transactionStatus", out var transactionStatus) || transactionStatus is not (1 or 2) ||
            !ValidReference(Text(item, "paymentTransactionId"))) return new(PaymentStatus.Review);
        return new(PaymentStatus.Succeeded, reference, Text(item, "paymentTransactionId"));
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        if (path is not (InitializePath or RetrievePath)) throw new InvalidOperationException("Unsupported payment operation.");
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, ServerConfiguration.SandboxUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization",
            IyzicoAuthorization.Create(configuration.ApiKey!, configuration.SecretKey!, nonce, path, json));
        request.Headers.Add("x-iyzi-rnd", nonce);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Sandbox HTTP request was rejected.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + count > 1024 * 1024) throw new HttpRequestException("Sandbox response too large.");
            await memory.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    internal static bool ValidCheckoutUrl(string? url) => url is { Length: <= 4096 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host == "sandbox-cpp.iyzipay.com" || uri.Host == "sandbox-api.iyzipay.com" && uri.AbsolutePath.StartsWith("/checkoutform/", StringComparison.Ordinal));
    internal static bool CheckoutUrlMatchesToken(string? url, string token)
    {
        if (!ValidCheckoutUrl(url)) return false;
        var uri = new Uri(url!);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("token", out var values) && values.Count == 1 && values[0] == token ||
            uri.Host == "sandbox-api.iyzipay.com" && uri.AbsolutePath.EndsWith("/" + Uri.EscapeDataString(token), StringComparison.Ordinal);
    }
    internal static bool ValidToken(string? token) => token is { Length: > 0 and <= 2048 } && token.All(c => c is >= '!' and <= '~');
    internal static string Money(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    internal static bool VerifySignature(string secret, string? signature, params string[] parts)
    {
        if (!ServerConfiguration.IsHash(signature)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(string.Join(':', parts)));
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature!));
    }
    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Decimal(JsonElement root, string name, out decimal result)
    {
        result = 0;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return false;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out result) :
            value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }
    private static bool Integer(JsonElement root, string name, out int result)
    {
        result = 0;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
    private static bool ValidReference(string? value) => value is { Length: > 0 and <= 180 } && value.All(char.IsAsciiDigit);
    private static string SafeError(JsonElement root)
    {
        var code = Text(root, "errorCode");
        return code is { Length: > 0 and <= 10 } && code.All(char.IsAsciiDigit) ? code : "bilinmiyor";
    }
}
