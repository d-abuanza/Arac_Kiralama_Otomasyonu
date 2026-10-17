using System.Globalization;
using System.Net.Mail;

namespace CarRentalAutomation.Payments.Api;

public sealed record CreateBookingRequest(long CarId, Guid RequestId);
public sealed record StartCheckoutRequest(Guid RequestId, BuyerDetails Buyer);
public sealed record BuyerDetails(string Name, string Surname, string IdentityNumber, string Email,
    string PhoneNumber, string Address, string City, string Country, string? ZipCode = null)
{
    internal void Validate()
    {
        foreach (var (value, max) in new[] { (Name, 80), (Surname, 80), (IdentityNumber, 30),
            (Email, 254), (PhoneNumber, 20), (Address, 500), (City, 100), (Country, 100) })
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
                throw new PaymentApiException(400, "Alıcı ve fatura bilgilerini eksiksiz, geçerli uzunlukta girin.");
        if (!MailAddress.TryCreate(Email, out var email) || email.Address != Email ||
            PhoneNumber[0] != '+' || PhoneNumber.Length is < 11 or > 16 || !PhoneNumber[1..].All(char.IsAsciiDigit) ||
            ZipCode?.Length > 20)
            throw new PaymentApiException(400, "E-posta, ülke kodlu telefon veya posta kodu geçersiz.");
    }
    public override string ToString() => "Buyer details [redacted]";
}

public sealed record BookingView(long Id, string Model, decimal Amount, string Currency, string Status, DateTime CreatedAt);
public sealed record PaymentView(long Id, long BookingId, string Model, decimal Amount, string Currency,
    string Provider, string Status, bool IsSimulation, DateTime CreatedAt, string? ProviderReference,
    bool CancelRequested, DateTime? ExpiresAt);
public sealed record CheckoutView(PaymentView Payment, string? CheckoutUrl);

internal sealed record BookingRow(long Id, long UserId, long? CarId, string Model, decimal Amount,
    string Currency, string Status, bool IsSimulation, DateTime CreatedAt)
{
    internal BookingView View => new(Id, Model, Amount, Currency, Status, CreatedAt);
}

internal sealed record PaymentRow(long Id, BookingRow Booking, decimal Amount, string Currency,
    string Provider, string Status, bool IsSimulation, DateTime CreatedAt, string? ProviderReference,
    string? ProtectedCheckout, string? TokenHash, DateTime? ExpiresAt, bool CancelRequested)
{
    internal PaymentView View => new(Id, Booking.Id, Booking.Model, Amount, Currency, Provider, Status,
        IsSimulation, CreatedAt, ProviderReference, CancelRequested, ExpiresAt);
    internal string ConversationId => "payment-" + Id.ToString(CultureInfo.InvariantCulture);
    internal string BasketId => "booking-" + Booking.Id.ToString(CultureInfo.InvariantCulture);
    internal string ItemId => "rental-" + Booking.Id.ToString(CultureInfo.InvariantCulture);
}

internal sealed class CheckoutState(string token, string url, DateTime expiresAt)
{
    public string Token { get; } = token;
    public string Url { get; } = url;
    public DateTime ExpiresAt { get; } = expiresAt;
    public override string ToString() => "Protected checkout state [redacted]";
}

internal sealed record ProviderResult(string Status, string? PaymentReference = null, string? TransactionReference = null);
internal sealed class PaymentApiException(int statusCode, string message) : Exception(message)
{
    internal int StatusCode { get; } = statusCode;
}

internal static class PaymentStatus
{
    internal const string Initializing = "Initializing", Pending = "Pending", Review = "VerificationRequired",
        Succeeded = "Succeeded", Failed = "Failed", Cancelled = "Cancelled", Expired = "Expired";
    internal static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled or Expired;
    internal static bool IsSuccess(string status) => status is Succeeded or "Success" or "SimulatedSuccess";
}
