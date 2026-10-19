using MySqlConnector;
using System.Data;

namespace CarRentalAutomation.Payments.Api;

internal interface IPaymentRepository
{
    Task<BookingRow> CreateBookingAsync(long userId, CreateBookingRequest request, CancellationToken ct);
    Task<(PaymentRow Payment, bool Created)> AcquireAttemptAsync(long userId, long bookingId, Guid requestId, CancellationToken ct);
    Task<PaymentRow> GetAsync(long paymentId, long? userId, CancellationToken ct);
    Task<PaymentRow?> FindTokenAsync(string hash, CancellationToken ct);
    Task SaveCheckoutAsync(long paymentId, string protectedCheckout, string tokenHash, DateTime expiresAt, CancellationToken ct);
    Task<PaymentRow> ApplyAsync(long paymentId, ProviderResult result, CancellationToken ct);
    Task RequestCancelAsync(long paymentId, long userId, CancellationToken ct);
    Task<IReadOnlyList<PaymentView>> HistoryAsync(long userId, int offset, int limit, CancellationToken ct, string? status = null);
    Task<bool> CheckSchemaAsync(CancellationToken ct);
}

internal sealed class MySqlPaymentRepository(ServerConfiguration configuration) : IPaymentRepository
{
    private const string BookingColumns = "Id,UserId,CarId,ModelSnapshot,Amount,Currency,Status,IsSimulation,CreatedAt";
    private const string PaymentSelect = """
        SELECT p.Id,p.BookingId,p.Amount,p.Currency,p.Provider,p.Status,p.IsSimulation,p.CreatedAt,
        p.ProviderReference,p.ProtectedCheckout,p.CheckoutTokenHash,p.CheckoutExpiresAt,p.CancelRequested,
        b.UserId,b.CarId,b.ModelSnapshot,b.Amount AS BookingAmount,b.Currency AS BookingCurrency,
        b.Status AS BookingStatus,b.IsSimulation AS BookingSimulation,b.CreatedAt AS BookingCreated
        FROM payments p JOIN bookings b ON b.Id=p.BookingId
        """;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration.ConnectionString))
            throw new PaymentApiException(503, "Veritabanı bağlantısı yapılandırılmadı.");
        var connection = new MySqlConnection(configuration.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<BookingRow> CreateBookingAsync(long userId, CreateBookingRequest request, CancellationToken ct)
    {
        if (request.CarId <= 0 || request.RequestId == Guid.Empty) throw new PaymentApiException(400, "Araç ve istek kimliği gerekli.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var user = Command(connection, transaction, "SELECT Id FROM users WHERE Id=@user FOR UPDATE", ("user", userId)))
            if (await user.ExecuteScalarAsync(ct) is null) throw new PaymentApiException(401, "Kullanıcı hesabı bulunamadı.");
        var existing = await FindBookingRequestAsync(connection, transaction, userId, request, ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return existing;
        }
        var offer = await LockOfferAsync(connection, transaction, userId, request.CarId, ct);
        var id = await InsertBookingAsync(connection, transaction, userId, request, offer, ct);
        await transaction.CommitAsync(ct);
        return new(id, userId, request.CarId, offer.Model, offer.Amount, offer.Currency, "PendingPayment", true, DateTime.UtcNow);
    }

    private static async Task<BookingRow?> FindBookingRequestAsync(MySqlConnection connection, MySqlTransaction transaction,
        long userId, CreateBookingRequest request, CancellationToken ct)
    {
        await using var existing = Command(connection, transaction,
            "SELECT " + BookingColumns + " FROM bookings WHERE UserId=@user AND RequestId=@request",
            ("user", userId), ("request", request.RequestId.ToString()));
        await using var reader = await existing.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var booking = ReadBooking(reader);
        if (booking.CarId != request.CarId) throw new PaymentApiException(409, "Bu istek kimliği başka bir araç için kullanıldı.");
        return booking;
    }

    private sealed record SavedOffer(string Model, string Company, decimal Amount, string Currency,
        string Location, DateTime Pickup, DateTime Return);

    private static async Task<SavedOffer> LockOfferAsync(MySqlConnection connection, MySqlTransaction transaction,
        long userId, long carId, CancellationToken ct)
    {
        await using var car = Command(connection, transaction, """
            SELECT c.Model,c.RentalCompany,c.Price,c.Currency,c.IsMockData,
            l.PickupLocation,l.PickupDate,l.ReturnDate FROM cars c
            JOIN collections l ON l.Id=c.CollectionId WHERE c.Id=@car AND l.UserId=@user FOR UPDATE
            """, ("car", carId), ("user", userId));
        await using var reader = await car.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PaymentApiException(404, "Kayıtlı araç bulunamadı.");
        var offer = new SavedOffer(reader.GetString("Model"), reader.GetString("RentalCompany"), reader.GetDecimal("Price"),
            reader.GetString("Currency"), reader.GetString("PickupLocation"), reader.GetDateTime("PickupDate"), reader.GetDateTime("ReturnDate"));
        if (reader.GetBoolean("IsMockData") || offer.Amount <= 0 || offer.Currency is not ("TRY" or "USD" or "EUR" or "GBP" or "NOK" or "CHF") ||
            offer.Pickup <= DateTime.Now || offer.Return <= offer.Pickup)
            throw new PaymentApiException(409, "Araç teklifi kiralama için uygun değil. Tarihleri ve fiyatı güncelleyin.");
        return offer;
    }

    private static async Task<long> InsertBookingAsync(MySqlConnection connection, MySqlTransaction transaction,
        long userId, CreateBookingRequest request, SavedOffer offer, CancellationToken ct)
    {
        await using var insert = Command(connection, transaction, """
            INSERT INTO bookings (UserId,CarId,ModelSnapshot,RentalCompanySnapshot,PickupLocation,PickupDate,ReturnDate,
            Amount,Currency,Status,IsSimulation,CreatedAt,RequestId)
            VALUES (@user,@car,@model,@company,@location,@pickup,@return,@amount,@currency,'PendingPayment',1,UTC_TIMESTAMP(6),@request)
            """, ("user", userId), ("car", request.CarId), ("model", offer.Model), ("company", offer.Company),
            ("location", offer.Location), ("pickup", offer.Pickup), ("return", offer.Return), ("amount", offer.Amount), ("currency", offer.Currency),
            ("request", request.RequestId.ToString()));
        await insert.ExecuteNonQueryAsync(ct);
        return insert.LastInsertedId;
    }

    public async Task<(PaymentRow Payment, bool Created)> AcquireAttemptAsync(long userId, long bookingId, Guid requestId, CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw new PaymentApiException(400, "Ödeme isteği kimliği gerekli.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var booking = await LockBookingAsync(connection, transaction, bookingId, userId, ct);
        if (!booking.IsSimulation || booking.Amount <= 0)
            throw new PaymentApiException(409, "Yalnızca Sandbox kiralama simülasyonu ödenebilir.");
        await using (var existing = Command(connection, transaction,
            PaymentSelect + " WHERE p.BookingId=@booking AND (p.RequestId=@request OR p.Status IN ('Initializing','Pending','VerificationRequired','Succeeded','Success','SimulatedSuccess')) ORDER BY (p.RequestId=@request) DESC,p.Id DESC LIMIT 1",
            ("booking", bookingId), ("request", requestId.ToString())))
        {
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var payment = ReadPayment(reader);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return (payment, false);
            }
        }
        if (booking.Status is "Confirmed" or "Paid") throw new PaymentApiException(409, "Bu rezervasyon zaten ödendi.");
        long id;
        await using (var insert = Command(connection, transaction, """
            INSERT INTO payments (BookingId,Amount,Currency,Provider,Status,IsSimulation,CreatedAt,RequestId)
            VALUES (@booking,@amount,@currency,'iyzico','Initializing',1,UTC_TIMESTAMP(6),@request)
            """, ("booking", bookingId), ("amount", booking.Amount), ("currency", booking.Currency), ("request", requestId.ToString())))
        {
            await insert.ExecuteNonQueryAsync(ct);
            id = insert.LastInsertedId;
        }
        await using (var update = Command(connection, transaction, "UPDATE bookings SET Status='PendingPayment' WHERE Id=@id", ("id", bookingId)))
            await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, userId, ct), true);
    }

    public async Task<PaymentRow> GetAsync(long paymentId, long? userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, PaymentSelect + " WHERE p.Id=@id" + (userId.HasValue ? " AND b.UserId=@user" : ""),
            ("id", paymentId), ("user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PaymentApiException(404, "Ödeme bulunamadı.");
        return ReadPayment(reader);
    }

    public async Task<PaymentRow?> FindTokenAsync(string hash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, PaymentSelect + " WHERE p.CheckoutTokenHash=@hash", ("hash", hash));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPayment(reader) : null;
    }

    public async Task SaveCheckoutAsync(long paymentId, string protectedCheckout, string tokenHash, DateTime expiresAt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, """
            UPDATE payments SET ProtectedCheckout=@state,CheckoutTokenHash=@hash,CheckoutExpiresAt=@expires,Status='Pending'
            WHERE Id=@id AND Status='Initializing' AND ProtectedCheckout IS NULL
            """, ("state", protectedCheckout), ("hash", tokenHash), ("expires", expiresAt), ("id", paymentId));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new PaymentApiException(409, "Ödeme durumu değişti; mevcut denemeyi doğrulayın.");
    }

    public async Task<PaymentRow> ApplyAsync(long paymentId, ProviderResult result, CancellationToken ct)
    {
        var initial = await GetAsync(paymentId, null, ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockBookingAsync(connection, transaction, initial.Booking.Id, initial.Booking.UserId, ct);
        var current = await LockPaymentAsync(connection, transaction, paymentId, ct);
        if (PaymentStatus.IsSuccess(current.Status) || PaymentStatus.IsTerminal(current.Status) && result.Status == PaymentStatus.Review)
        {
            await transaction.CommitAsync(ct);
            return current;
        }
        var status = await ResolveProviderStatusAsync(connection, transaction, current, result.Status, ct);
        await UpdatePaymentAndBookingAsync(connection, transaction, current, result, status, ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(paymentId, current.Booking.UserId, ct);
    }

    private static async Task<PaymentRow> LockPaymentAsync(MySqlConnection connection, MySqlTransaction transaction,
        long paymentId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, PaymentSelect + " WHERE p.Id=@id FOR UPDATE", ("id", paymentId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadPayment(reader);
    }

    private static async Task<string> ResolveProviderStatusAsync(MySqlConnection connection, MySqlTransaction transaction,
        PaymentRow current, string status, CancellationToken ct)
    {
        if (status is not (PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Cancelled or PaymentStatus.Expired or PaymentStatus.Review))
            throw new InvalidOperationException("Unsupported provider state.");
        if (status == PaymentStatus.Failed && current.CancelRequested) status = PaymentStatus.Cancelled;
        // Never silently accept a second successful charge for a single booking.
        await using (var duplicate = Command(connection, transaction,
            "SELECT COUNT(*) FROM payments WHERE BookingId=@booking AND Id<>@id AND Status IN ('Succeeded','Success','SimulatedSuccess')",
            ("booking", current.Booking.Id), ("id", current.Id)))
            if (status == PaymentStatus.Succeeded && Convert.ToInt64(await duplicate.ExecuteScalarAsync(ct)) > 0)
                status = PaymentStatus.Review;
        return status;
    }

    private static async Task UpdatePaymentAndBookingAsync(MySqlConnection connection, MySqlTransaction transaction,
        PaymentRow current, ProviderResult result, string status, CancellationToken ct)
    {
        await using (var command = Command(connection, transaction, """
            UPDATE payments SET Status=@status,ProviderReference=COALESCE(@reference,ProviderReference),
            ProviderTransactionReference=COALESCE(@transaction,ProviderTransactionReference) WHERE Id=@id
            """, ("status", status), ("reference", result.PaymentReference), ("transaction", result.TransactionReference), ("id", current.Id)))
            await command.ExecuteNonQueryAsync(ct);
        var bookingStatus = status == PaymentStatus.Succeeded ? "Confirmed" : status == PaymentStatus.Review ? "PaymentVerificationRequired" :
            status == PaymentStatus.Cancelled ? "PaymentCancelled" : status == PaymentStatus.Expired ? "PaymentExpired" : "PaymentFailed";
        await using (var command = Command(connection, transaction,
            "UPDATE bookings SET Status=@status WHERE Id=@id AND Status NOT IN ('Confirmed','Paid')",
            ("status", bookingStatus), ("id", current.Booking.Id))) await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RequestCancelAsync(long paymentId, long userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, """
            UPDATE payments p JOIN bookings b ON b.Id=p.BookingId SET p.CancelRequested=1
            WHERE p.Id=@id AND b.UserId=@user AND p.Status NOT IN ('Succeeded','Success','SimulatedSuccess')
            """, ("id", paymentId), ("user", userId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PaymentView>> HistoryAsync(long userId, int offset, int limit, CancellationToken ct, string? status = null)
    {
        status = string.IsNullOrWhiteSpace(status) ? null : status;
        if (status is not null && status is not ("Initializing" or "Pending" or "Succeeded" or "Failed" or "Cancelled" or "Expired" or "VerificationRequired" or "Success" or "SimulatedSuccess"))
            throw new PaymentApiException(400, "Geçersiz ödeme durumu seçildi.");
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null,
            PaymentSelect + " WHERE b.UserId=@user AND (@status IS NULL OR p.Status=@status) ORDER BY p.CreatedAt DESC,p.Id DESC LIMIT @limit OFFSET @offset",
            ("user", userId), ("status", (object?)status ?? DBNull.Value), ("limit", Math.Clamp(limit, 1, 100)), ("offset", Math.Clamp(offset, 0, 100000)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PaymentView>();
        while (await reader.ReadAsync(ct)) result.Add(ReadPayment(reader).View);
        return result;
    }

    public async Task<bool> CheckSchemaAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            await using (var command = Command(connection, null, PaymentSelect + " LIMIT 0"))
            await using (await command.ExecuteReaderAsync(ct)) { }
            await using (var command = Command(connection, null,
                "SELECT RentalCompanySnapshot,PickupLocation,PickupDate,ReturnDate,RequestId FROM bookings LIMIT 0"))
            await using (await command.ExecuteReaderAsync(ct)) { }
            await using (var command = Command(connection, null,
                "SELECT RequestId,ProviderTransactionReference FROM payments LIMIT 0"))
            await using (await command.ExecuteReaderAsync(ct)) { }
            await using (var command = Command(connection, null, """
                SELECT TABLE_NAME,INDEX_NAME,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',') AS ColumnsInIndex
                FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND NON_UNIQUE=0
                AND TABLE_NAME IN ('bookings','payments') GROUP BY TABLE_NAME,INDEX_NAME
                """))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(ct)) indexes.Add(reader.GetString(0) + ":" + reader.GetString(2));
                return new[] { "bookings:UserId,RequestId", "payments:BookingId,RequestId", "payments:CheckoutTokenHash", "payments:Provider,ProviderReference" }.All(indexes.Contains);
            }
        }
        catch (Exception ex) when (ex is MySqlException or PaymentApiException or InvalidOperationException) { return false; }
    }

    private static async Task<BookingRow> LockBookingAsync(MySqlConnection connection, MySqlTransaction transaction, long bookingId, long userId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT " + BookingColumns + " FROM bookings WHERE Id=@id AND UserId=@user FOR UPDATE",
            ("id", bookingId), ("user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PaymentApiException(404, "Rezervasyon bulunamadı.");
        return ReadBooking(reader);
    }
    private static BookingRow ReadBooking(MySqlDataReader r) => new(r.GetInt64("Id"), r.GetInt64("UserId"),
        r.IsDBNull(r.GetOrdinal("CarId")) ? null : r.GetInt64("CarId"), r.GetString("ModelSnapshot"),
        r.GetDecimal("Amount"), r.GetString("Currency"), r.GetString("Status"), r.GetBoolean("IsSimulation"), r.GetDateTime("CreatedAt"));
    private static PaymentRow ReadPayment(MySqlDataReader r)
    {
        var booking = new BookingRow(r.GetInt64("BookingId"), r.GetInt64("UserId"),
            r.IsDBNull(r.GetOrdinal("CarId")) ? null : r.GetInt64("CarId"), r.GetString("ModelSnapshot"),
            r.GetDecimal("BookingAmount"), r.GetString("BookingCurrency"), r.GetString("BookingStatus"), r.GetBoolean("BookingSimulation"), r.GetDateTime("BookingCreated"));
        return new(r.GetInt64("Id"), booking, r.GetDecimal("Amount"), r.GetString("Currency"), r.GetString("Provider"),
            r.GetString("Status"), r.GetBoolean("IsSimulation"), r.GetDateTime("CreatedAt"), NullableString(r, "ProviderReference"),
            NullableString(r, "ProtectedCheckout"), NullableString(r, "CheckoutTokenHash"),
            r.IsDBNull(r.GetOrdinal("CheckoutExpiresAt")) ? null : DateTime.SpecifyKind(r.GetDateTime("CheckoutExpiresAt"), DateTimeKind.Utc), r.GetBoolean("CancelRequested"));
    }
    private static string? NullableString(MySqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(name);
    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new MySqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue("@" + parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
}
