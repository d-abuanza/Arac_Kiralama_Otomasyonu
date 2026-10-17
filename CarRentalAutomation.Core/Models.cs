namespace CarRentalAutomation.Core;

public sealed record SearchCriteria(string PickupLocation, DateTime PickupAt, DateTime ReturnAt)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PickupLocation)) throw new ArgumentException("Alış lokasyonu zorunludur.");
        if (PickupAt <= DateTime.Now) throw new ArgumentException("Alış tarihi gelecekte olmalıdır.");
        if (ReturnAt <= PickupAt) throw new ArgumentException("Dönüş tarihi alış tarihinden sonra olmalıdır.");
    }
}

public sealed record CarOffer(string Model, string RentalCompany, string Transmission, string FuelType,
    decimal Price, string Currency, string? SourceUrl, bool IsMockData = false)
{
    public DateTime? ScrapedAt { get; init; }
}

public sealed record SavedCollection(long Id, string Name, SearchCriteria Criteria, DateTime CreatedAt,
    IReadOnlyList<CarOffer> Cars);

public sealed record Booking(long Id, string Model, decimal Amount, string Currency, string Status, DateTime CreatedAt);
public sealed record Payment(long Id, long BookingId, decimal Amount, string Currency, string Provider,
    string Status, bool IsSimulation, DateTime CreatedAt);

public interface ICarSearchProvider
{
    Task<IReadOnlyList<CarOffer>> SearchAsync(SearchCriteria criteria, CancellationToken cancellationToken = default);
}
public interface ICollectionRepository
{
    Task<IReadOnlyList<SavedCollection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedCollection> SaveAsync(string name, SearchCriteria criteria, IReadOnlyList<CarOffer> cars, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}
public interface IBookingService
{
    Task<Booking> CreateSimulatedBookingAsync(CarOffer offer, CancellationToken cancellationToken = default);
    Task<Payment> PayAsync(Booking booking, CancellationToken cancellationToken = default);
}
public interface IPngReportExporter
{
    void Export(SavedCollection collection, string path);
}
public interface ISiteCredentialRepository
{
    Task<string?> GetPhoneNumberAsync(long userId, CancellationToken cancellationToken = default);
}

public interface IDatabaseConnectionCheck
{
    Task<(bool Success, string Message)> CheckAsync(CancellationToken cancellationToken = default);
}

public enum Yolcu360SessionState
{
    Initializing, LoadingLoginPage, WaitingForPhoneField, SubmittingPhone,
    WaitingForSecurityChallenge, WaitingForOtp, VerifyingOtp, Authenticated,
    Searching, ExtractingResults, AuthenticationFailed, SearchFailed, Cancelled
}

public sealed record BrowserOperationResult(Yolcu360SessionState State, string Message, bool Succeeded = false);
