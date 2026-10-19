using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

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
