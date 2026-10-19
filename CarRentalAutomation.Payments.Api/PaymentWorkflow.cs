using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CarRentalAutomation.Payments.Api;

internal sealed class PaymentWorkflow(IPaymentRepository repository, ICheckoutProvider provider, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("CarRentalAutomation.SandboxCheckout.v1");

    internal async Task<CheckoutView> StartAsync(long userId, long bookingId, StartCheckoutRequest request, string ip, CancellationToken ct)
    {
        if (request.Buyer is null) throw new PaymentApiException(400, "Alıcı bilgileri gerekli.");
        request.Buyer.Validate();
        var (payment, created) = await repository.AcquireAttemptAsync(userId, bookingId, request.RequestId, ct);
        if (!created) return Checkout(payment);
        try
        {
            var state = await provider.InitializeAsync(payment, request.Buyer, ip, ct);
            var encrypted = _protector.Protect(JsonSerializer.Serialize(state));
            await repository.SaveCheckoutAsync(payment.Id, encrypted, ServerConfiguration.Hash(state.Token), state.ExpiresAt, ct);
            return Checkout(await repository.GetAsync(payment.Id, userId, ct));
        }
        catch (ProviderRejectedException exception)
        {
            await repository.ApplyAsync(payment.Id, new(PaymentStatus.Failed), CancellationToken.None);
            throw new PaymentApiException(502, exception.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or PaymentApiException or CryptographicException or IOException)
        {
            await repository.ApplyAsync(payment.Id, new(PaymentStatus.Review), CancellationToken.None);
            throw new PaymentApiException(502, "Ödeme formunun sonucu doğrulanamadı. Aynı rezervasyon için yeni ödeme başlatmadan mevcut denemeyi kontrol edin.");
        }
    }

    internal async Task<PaymentView> VerifyAsync(long paymentId, long userId, CancellationToken ct)
    {
        var payment = await repository.GetAsync(paymentId, userId, ct);
        return (await VerifyRowAsync(payment, ct)).View;
    }

    internal async Task<CheckoutView> ResumeAsync(long paymentId, long userId, CancellationToken ct) =>
        Checkout(await repository.GetAsync(paymentId, userId, ct));

    internal async Task<PaymentView> CancelAsync(long paymentId, long userId, CancellationToken ct)
    {
        await repository.GetAsync(paymentId, userId, ct);
        await repository.RequestCancelAsync(paymentId, userId, ct);
        return await VerifyAsync(paymentId, userId, ct);
    }

    internal async Task HandleCallbackAsync(string token, CancellationToken ct)
    {
        if (!IyzicoCheckoutProvider.ValidToken(token)) throw new PaymentApiException(400, "Geçersiz ödeme bildirimi.");
        var payment = await repository.FindTokenAsync(ServerConfiguration.Hash(token), ct)
            ?? throw new PaymentApiException(404, "Ödeme bildirimi eşleştirilemedi.");
        var state = Unprotect(payment);
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state.Token), System.Text.Encoding.UTF8.GetBytes(token)))
            throw new PaymentApiException(400, "Ödeme bildirimi eşleştirilemedi.");
        await VerifyRowAsync(payment, ct);
    }

    private async Task<PaymentRow> VerifyRowAsync(PaymentRow payment, CancellationToken ct)
    {
        if (PaymentStatus.IsSuccess(payment.Status)) return payment;
        if (payment.ProtectedCheckout is null)
        {
            // A crashed initialization must block a duplicate attempt; its outcome is unknown.
            if (payment.Status == PaymentStatus.Initializing && payment.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
                return await repository.ApplyAsync(payment.Id, new(PaymentStatus.Review), ct);
            return payment;
        }
        ProviderResult result;
        try { result = await provider.RetrieveAsync(payment, Unprotect(payment), ct); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or CryptographicException or PaymentApiException or IOException)
        { result = new(PaymentStatus.Review); }
        return await repository.ApplyAsync(payment.Id, result, CancellationToken.None);
    }

    private CheckoutView Checkout(PaymentRow payment)
    {
        string? url = null;
        if (payment.Status == PaymentStatus.Pending && !payment.CancelRequested && payment.ExpiresAt > DateTime.UtcNow)
        {
            var state = Unprotect(payment);
            if (!IyzicoCheckoutProvider.CheckoutUrlMatchesToken(state.Url, state.Token)) throw new PaymentApiException(409, "Ödeme bağlantısı doğrulanamadı.");
            url = state.Url;
        }
        return new(payment.View, url);
    }

    private CheckoutState Unprotect(PaymentRow payment)
    {
        try
        {
            if (payment.ProtectedCheckout is null) throw new CryptographicException();
            var state = JsonSerializer.Deserialize<CheckoutState>(_protector.Unprotect(payment.ProtectedCheckout));
            if (state is null || !IyzicoCheckoutProvider.ValidToken(state.Token) ||
                ServerConfiguration.Hash(state.Token) != payment.TokenHash) throw new CryptographicException();
            return state;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        { throw new PaymentApiException(409, "Korunan ödeme oturumu okunamadı. Sunucu anahtar deposunu geri yükleyin; yeni ödeme başlatmayın."); }
    }
}

internal sealed class ProviderRejectedException(string message) : Exception(message);
