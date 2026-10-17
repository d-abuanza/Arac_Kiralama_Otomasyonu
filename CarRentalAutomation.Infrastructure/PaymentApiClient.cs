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
    public async Task<IReadOnlyList<RentalPayment>> HistoryAsync(int offset, CancellationToken ct) =>
        await SendAsync<List<RentalPayment>>(HttpMethod.Get, $"api/payments?offset={Math.Max(0, offset)}&limit=100", null, ct);

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
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + read > 1024 * 1024) throw new PaymentClientException("Ödeme sunucusu beklenmeyen büyüklükte yanıt verdi.");
                await memory.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == HttpStatusCode.Unauthorized ? "Ödeme erişimi doğrulanamadı. Yerel backend kurulumunu kontrol edin." : "Ödeme işlemi tamamlanamadı. Mevcut denemenin durumunu kontrol edin.";
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(memory.ToArray());
                        if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 700 } text &&
                            !text.Contains(token, StringComparison.Ordinal) && !text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Any(char.IsControl)) message = text;
                    }
                    catch (JsonException) { }
                }
                throw new PaymentClientException(message, (int)response.StatusCode);
            }
            return JsonSerializer.Deserialize<T>(memory.ToArray(), Json) ?? throw new PaymentClientException("Ödeme sunucusunun yanıtı boş.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PaymentClientException("Ödeme isteği zaman aşımına uğradı. Sonuç belirsiz; aynı araçtan devam ederek mevcut denemeyi kontrol edin."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        { throw new PaymentClientException("Ödeme sunucusuna ulaşılamadı veya yanıt okunamadı. Backend ve ngrok açık olmalı. Sonucu doğrulamadan yeni kiralama başlatmayın."); }
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

public sealed class FileRentalAttemptStore : IRentalAttemptStore
{
    private readonly string _directory;
    public FileRentalAttemptStore(string serverAddress, long userId, string? directory = null)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverAddress.TrimEnd('/') + "|" + userId)))[..24];
        _directory = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarRentalAutomation", "Payments", "Attempts"), scope);
    }
    public async Task<RentalAttempt?> ReadAsync(long carId, CancellationToken ct)
    {
        var file = FileFor(carId);
        if (!File.Exists(file)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<RentalAttempt>(await File.ReadAllTextAsync(file, ct));
            if (result is null || result.CarId != carId || result.BookingRequestId == Guid.Empty || result.PaymentRequestId == Guid.Empty) throw new JsonException();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new PaymentClientException("Yerel kiralama kaydı okunamadı. Tekrar ödeme yapmadan mevcut rezervasyonu kontrol edin; bu kaydı silmeyin."); }
    }
    public async Task SaveAsync(RentalAttempt attempt, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var file = FileFor(attempt.CarId);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(attempt), ct);
        File.Move(temporary, file, overwrite: true);
    }
    private string FileFor(long carId) => carId > 0 ? Path.Combine(_directory, carId + ".json") : throw new ArgumentOutOfRangeException(nameof(carId));
}
