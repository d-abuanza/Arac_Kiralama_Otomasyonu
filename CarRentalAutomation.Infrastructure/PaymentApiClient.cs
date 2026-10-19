using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

public sealed class PaymentClientException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed class PaymentApiClient : IRentalPaymentApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _address;
    private readonly Func<CancellationToken, Task<string?>> _credential;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public PaymentApiClient(string address, HttpClient? http = null, Func<CancellationToken, Task<string?>>? credential = null)
    {
        _address = address;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(45) };
        _credential = credential ?? ReadLocalCredentialAsync;
    }
    public Task<RentalBooking> CreateBookingAsync(long carId, Guid requestId, CancellationToken ct) =>
        SendAsync<RentalBooking>(HttpMethod.Post, "api/bookings", new { carId, requestId }, ct);
    public Task<RentalCheckout> StartCheckoutAsync(long bookingId, Guid requestId, RentalBuyer buyer, CancellationToken ct) =>
        SendAsync<RentalCheckout>(HttpMethod.Post, $"api/bookings/{bookingId}/checkout", new { requestId, buyer }, ct);
    public Task<RentalCheckout> ResumeAsync(long paymentId, CancellationToken ct) => SendAsync<RentalCheckout>(HttpMethod.Get, $"api/payments/{paymentId}/checkout", null, ct);
    public Task<RentalPayment> GetAsync(long paymentId, CancellationToken ct) => SendAsync<RentalPayment>(HttpMethod.Get, $"api/payments/{paymentId}", null, ct);
    public Task<RentalPayment> VerifyAsync(long paymentId, CancellationToken ct) => SendAsync<RentalPayment>(HttpMethod.Post, $"api/payments/{paymentId}/verify", new { }, ct);
    public Task<RentalPayment> CancelAsync(long paymentId, CancellationToken ct) => SendAsync<RentalPayment>(HttpMethod.Post, $"api/payments/{paymentId}/cancel", new { }, ct);
    public async Task<IReadOnlyList<RentalPayment>> HistoryAsync(int offset, CancellationToken ct, string? status = null)
    {
        var records = await SendAsync<List<RentalPayment>>(HttpMethod.Get,
            $"api/payments?offset={Math.Max(0, offset)}&limit=100&status={Uri.EscapeDataString(status ?? "")}", null, ct);
        if (!string.IsNullOrEmpty(status) && records.Any(payment => payment.Status != status))
            throw new PaymentClientException("Ödeme sunucusu seçilen durum filtresini uygulamadı. Ödeme hizmetini güncel sürümle yeniden başlatıp Yenile düğmesine basın.");
        return records;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!ValidBaseAddress(_address)) throw new PaymentClientException("Ödeme sunucusu adresi geçersiz. HTTPS veya yerel HTTP adresi kullanın.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var token = await _credential(timeout.Token);
            if (string.IsNullOrWhiteSpace(token)) throw new PaymentClientException("Ödeme erişimi hazır değil. scripts\\Start-PaymentApi.ps1 dosyasını çalıştırın; API anahtarlarını uygulamaya girmeyin.");
            if (token.Length is < 32 or > 512 || token.Any(c => c < '!' || c > '~')) throw new PaymentClientException("Yerel ödeme erişim değeri geçersiz. Backend yerel kurulumunu kontrol edin.");
            using var request = new HttpRequestMessage(method, new Uri(new Uri(_address.TrimEnd('/') + "/"), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var content = await ReadResponseAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw ReadErrorResponse(response.StatusCode, content, token);
            return JsonSerializer.Deserialize<T>(content, Json) ?? throw new PaymentClientException("Ödeme sunucusunun yanıtı boş.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PaymentClientException("Ödeme isteği zaman aşımına uğradı. Sonuç belirsiz; aynı araçtan devam ederek mevcut denemeyi kontrol edin."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        { throw new PaymentClientException("Ödeme sunucusuna ulaşılamadı veya yanıt okunamadı. Backend ve ngrok açık olmalı. Sonucu doğrulamadan yeni kiralama başlatmayın."); }
    }

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > 1024 * 1024) throw new PaymentClientException("Ödeme sunucusu beklenmeyen büyüklükte yanıt verdi.");
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.ToArray();
    }

    private static PaymentClientException ReadErrorResponse(HttpStatusCode statusCode, byte[] content, string token)
    {
        var message = statusCode == HttpStatusCode.Unauthorized ? "Ödeme erişimi doğrulanamadı. Yerel backend kurulumunu kontrol edin." : "Ödeme işlemi tamamlanamadı. Mevcut denemenin durumunu kontrol edin.";
        if (statusCode != HttpStatusCode.Unauthorized)
        {
            try
            {
                using var error = JsonDocument.Parse(content);
                if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 700 } text &&
                    !text.Contains(token, StringComparison.Ordinal) && !text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Any(char.IsControl)) message = text;
            }
            catch (JsonException) { }
        }
        return new PaymentClientException(message, (int)statusCode);
    }

    public static bool ValidBaseAddress(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static async Task<string?> ReadLocalCredentialAsync(CancellationToken ct)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarRentalAutomation", "Payments", "local-client.dpapi");
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { throw new PaymentClientException("Yerel ödeme erişimi bu Windows kullanıcısıyla açılamadı. Backend yerel kurulumunu yeniden çalıştırın."); }
    }
    public void Dispose() => _http.Dispose();
}
