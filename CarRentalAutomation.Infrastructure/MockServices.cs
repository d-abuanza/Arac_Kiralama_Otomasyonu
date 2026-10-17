using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

public sealed class MockCarSearchProvider : ICarSearchProvider
{
    public Task<IReadOnlyList<CarOffer>> SearchAsync(SearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CarOffer> offers =
        [
            new("Renault Clio", "Avis", "Otomatik", "Benzin", 1450m, "TRY", null, true),
            new("Fiat Egea", "Budget", "Manuel", "Dizel", 1190m, "TRY", null, true),
            new("Toyota Corolla", "Garenta", "Otomatik", "Hibrit", 2100m, "TRY", null, true),
            new("Peugeot 2008", "Enterprise", "Otomatik", "Benzin", 2350m, "TRY", null, true)
        ];
        return Task.FromResult(offers);
    }
}

public sealed class InMemoryCollectionRepository : ICollectionRepository
{
    private readonly List<SavedCollection> _items = [];
    private long _nextId = 1;

    public Task<IReadOnlyList<SavedCollection>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedCollection>>(_items.OrderByDescending(x => x.CreatedAt).ToArray());

    public Task<SavedCollection> SaveAsync(string name, SearchCriteria criteria, IReadOnlyList<CarOffer> cars, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Koleksiyon adı zorunludur.");
        if (_items.Any(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Bu isimde bir koleksiyon zaten var.");
        var item = new SavedCollection(_nextId++, name.Trim(), criteria, DateTime.Now, cars.ToArray());
        _items.Add(item);
        return Task.FromResult(item);
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.RemoveAll(x => x.Id == id);
        return Task.CompletedTask;
    }
}

public sealed class MockBookingService : IBookingService
{
    private long _bookingId;
    private long _paymentId;
    public Task<Booking> CreateSimulatedBookingAsync(CarOffer offer, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Booking(++_bookingId, offer.Model, offer.Price, offer.Currency, "Simulated", DateTime.Now));

    public Task<Payment> PayAsync(Booking booking, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Payment(++_paymentId, booking.Id, booking.Amount, booking.Currency,
            "MockPaymentGateway", "SimulatedSuccess", true, DateTime.Now));
}
