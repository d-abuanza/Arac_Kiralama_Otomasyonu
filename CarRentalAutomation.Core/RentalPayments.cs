namespace CarRentalAutomation.Core;

public sealed record RentalBuyer(string Name, string Surname, string IdentityNumber, string Email,
    string PhoneNumber, string Address, string City, string Country, string? ZipCode = null)
{
    public override string ToString() => "Buyer details [redacted]";
}
public sealed record RentalBooking(long Id, string Model, decimal Amount, string Currency, string Status, DateTime CreatedAt);
public sealed record RentalPayment(long Id, long BookingId, string Model, decimal Amount, string Currency,
    string Provider, string Status, bool IsSimulation, DateTime CreatedAt, string? ProviderReference,
    bool CancelRequested, DateTime? ExpiresAt);
public sealed record RentalCheckout(RentalPayment Payment, string? CheckoutUrl)
{
    public override string ToString() => $"Sandbox checkout for payment {Payment.Id} [URL redacted]";
}
public sealed record RentalAttempt(long CarId, Guid BookingRequestId, Guid PaymentRequestId,
    long? BookingId = null, long? PaymentId = null, string? LastStatus = null);

public interface IRentalPaymentApi
{
    Task<RentalBooking> CreateBookingAsync(long carId, Guid requestId, CancellationToken ct);
    Task<RentalCheckout> StartCheckoutAsync(long bookingId, Guid requestId, RentalBuyer buyer, CancellationToken ct);
    Task<RentalCheckout> ResumeAsync(long paymentId, CancellationToken ct);
    Task<RentalPayment> GetAsync(long paymentId, CancellationToken ct);
    Task<RentalPayment> VerifyAsync(long paymentId, CancellationToken ct);
    Task<RentalPayment> CancelAsync(long paymentId, CancellationToken ct);
    Task<IReadOnlyList<RentalPayment>> HistoryAsync(int offset, CancellationToken ct);
}
public interface IRentalAttemptStore
{
    Task<RentalAttempt?> ReadAsync(long carId, CancellationToken ct);
    Task SaveAsync(RentalAttempt attempt, CancellationToken ct);
}
public static class RentalPaymentStates
{
    public static bool Succeeded(string status) => status == "Succeeded";
    public static bool Failed(string status) => status is "Failed" or "Cancelled" or "Expired";
    public static bool Terminal(string status) => Succeeded(status) || Failed(status);
    public static string Label(string status) => status switch
    {
        "Succeeded" => "Başarılı", "Failed" => "Başarısız", "Cancelled" => "İptal edildi", "Expired" => "Süresi doldu",
        "Initializing" => "Ödeme formu hazırlanıyor", "VerificationRequired" => "Sonuç doğrulaması gerekiyor",
        "Pending" => "Beklemede", _ => "Durum kontrol edilmeli"
    };
}

/// <summary>Persists request IDs before network calls; never stores buyer data or checkout tokens.</summary>
public sealed class RentalPaymentCoordinator(IRentalPaymentApi api, IRentalAttemptStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<RentalAttempt?> ReadAsync(long carId, CancellationToken ct) => store.ReadAsync(carId, ct);

    public async Task<RentalBooking> EnsureBookingAsync(long carId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var attempt = await GetOrCreateAsync(carId, ct);
            var booking = await api.CreateBookingAsync(carId, attempt.BookingRequestId, ct);
            if (attempt.BookingId.HasValue && attempt.BookingId != booking.Id) throw new InvalidOperationException("Rezervasyon kimliği eşleşmedi; yeni ödeme başlatmayın.");
            await store.SaveAsync(attempt with { BookingId = booking.Id }, ct);
            return booking;
        }
        finally { _gate.Release(); }
    }

    public async Task<RentalCheckout> StartAsync(long carId, RentalBuyer buyer, bool retryFailed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var attempt = await store.ReadAsync(carId, ct) ?? throw new InvalidOperationException("Önce rezervasyonu oluşturun.");
            if (!attempt.BookingId.HasValue) throw new InvalidOperationException("Rezervasyon henüz doğrulanmadı.");
            if (retryFailed)
            {
                if (!attempt.PaymentId.HasValue) throw new InvalidOperationException("Önce mevcut ödeme denemesinin durumunu kontrol edin.");
                var current = await api.GetAsync(attempt.PaymentId.Value, ct);
                if (!RentalPaymentStates.Failed(current.Status)) throw new InvalidOperationException("Bu ödeme doğrulanmadan yeni deneme başlatılamaz.");
                attempt = attempt with { PaymentRequestId = Guid.NewGuid(), PaymentId = null, LastStatus = null };
                await store.SaveAsync(attempt, ct);
            }
            var checkout = await api.StartCheckoutAsync(attempt.BookingId.Value, attempt.PaymentRequestId, buyer, ct);
            if (checkout.Payment.BookingId != attempt.BookingId) throw new InvalidOperationException("Ödeme rezervasyonla eşleşmedi.");
            await store.SaveAsync(attempt with { PaymentId = checkout.Payment.Id, LastStatus = checkout.Payment.Status }, ct);
            return checkout;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordAsync(long carId, RentalPayment payment, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var attempt = await store.ReadAsync(carId, ct) ?? throw new InvalidOperationException("Yerel ödeme kaydı bulunamadı.");
            if (attempt.BookingId != payment.BookingId || attempt.PaymentId.HasValue && attempt.PaymentId != payment.Id)
                throw new InvalidOperationException("Ödeme kaydı eşleşmedi.");
            await store.SaveAsync(attempt with { PaymentId = payment.Id, LastStatus = payment.Status }, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task StartSeparateRentalAsync(long carId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var attempt = await store.ReadAsync(carId, ct);
            if (attempt?.PaymentId is not { } paymentId || !RentalPaymentStates.Succeeded((await api.GetAsync(paymentId, ct)).Status))
                throw new InvalidOperationException("Mevcut kiralamanın sonucu doğrulanmadan ayrı bir kiralama başlatılamaz.");
            await store.SaveAsync(new(carId, Guid.NewGuid(), Guid.NewGuid()), ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<RentalAttempt> GetOrCreateAsync(long carId, CancellationToken ct)
    {
        if (carId <= 0) throw new InvalidOperationException("Ödeme için araç veritabanına kaydedilmelidir.");
        var attempt = await store.ReadAsync(carId, ct);
        if (attempt is not null) return attempt;
        attempt = new(carId, Guid.NewGuid(), Guid.NewGuid());
        await store.SaveAsync(attempt, ct);
        return attempt;
    }
}
