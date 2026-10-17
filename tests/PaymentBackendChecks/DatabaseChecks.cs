using CarRentalAutomation.Payments.Api;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using System.Text.RegularExpressions;

internal static class DatabaseChecks
{
    // Opt-in only. No records are copied. All writes go into a new, uniquely named test database.
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new ConfigurationBuilder().AddUserSecrets(ServerConfiguration.SecretsId, reloadOnChange: false).Build();
        var sourceString = settings.GetConnectionString("CarRental") ?? throw new InvalidOperationException("Run the local backend setup first.");
        (settings as IDisposable)?.Dispose();
        var source = new MySqlConnectionStringBuilder(sourceString);
        if (!Regex.IsMatch(source.Database, "^[A-Za-z0-9_]+$")) throw new InvalidOperationException("Unsupported database name for isolated test.");
        var name = "carrental_stage2_test_" + Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(name, "^carrental_stage2_test_[0-9a-f]{32}$")) throw new InvalidOperationException("Unsafe test database name.");
        await using var admin = new MySqlConnection(sourceString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await Execute(admin, "CREATE DATABASE `" + name + "`");
            created = true;
            foreach (var table in new[] { "users", "collections", "cars", "bookings", "payments" })
                await Execute(admin, $"CREATE TABLE `{name}`.`{table}` LIKE `{source.Database}`.`{table}`");
            var isolated = new MySqlConnectionStringBuilder(sourceString) { Database = name, Pooling = false };
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:CarRental"] = isolated.ConnectionString,
                ["Iyzico:ApiKey"] = "sandbox-synthetic-api", ["Iyzico:SecretKey"] = "sandbox-synthetic-secret",
                ["Payments:CallbackUrl"] = "https://callback.example.test/api/payments/iyzico/callback",
                ["Payments:Clients:0:UserId"] = "1", ["Payments:Clients:0:TokenHash"] = ServerConfiguration.Hash("synthetic-client-token")
            };
            var config = new ServerConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            var repo = new MySqlPaymentRepository(config);
            await using var db = new MySqlConnection(isolated.ConnectionString);
            await db.OpenAsync();
            await Execute(db, "INSERT INTO users (Id,Username,PasswordHash) VALUES (1,'synthetic-one','unused'),(2,'synthetic-two','unused')");
            await using (var seed = new MySqlCommand("""
                INSERT INTO collections (Id,UserId,CollectionName,PickupLocation,PickupDate,ReturnDate,FuelFilter,TransmissionFilter)
                VALUES (1,1,'Synthetic one','Synthetic Airport',@pickup,@return,'Benzin','Otomatik'),
                (2,2,'Synthetic two','Synthetic Airport',@pickup,@return,'Benzin','Otomatik');
                INSERT INTO cars (Id,CollectionId,Model,RentalCompany,Transmission,FuelType,Price,Currency,IsMockData,ScrapedAt)
                VALUES (7,1,'Synthetic Car','Synthetic Company','Otomatik','Benzin',25.50,'TRY',0,UTC_TIMESTAMP(6)),
                (8,2,'Foreign User Car','Synthetic Company','Otomatik','Benzin',99.50,'TRY',0,UTC_TIMESTAMP(6));
                """, db))
            {
                seed.Parameters.AddWithValue("pickup", DateTime.Now.AddDays(7));
                seed.Parameters.AddWithValue("return", DateTime.Now.AddDays(10));
                await seed.ExecuteNonQueryAsync();
            }
            check(await repo.CheckSchemaAsync(default), "real SQL schema and unique indexes recognized in isolated database");
            var request = new CreateBookingRequest(7, Guid.NewGuid());
            var bookings = await Task.WhenAll(repo.CreateBookingAsync(1, request, default), repo.CreateBookingAsync(1, request, default));
            check(bookings[0].Id == bookings[1].Id, "concurrent booking idempotency");
            check(bookings[0].Amount == 25.50m, "database total retained without day multiplication");
            check(await Count(db, "bookings") == 1, "only one booking persisted");
            try { await repo.CreateBookingAsync(2, new(7, Guid.NewGuid()), default); check(false, "foreign car accepted"); }
            catch (PaymentApiException ex) { check(ex.StatusCode == 404, "foreign collection rejected in SQL"); }
            try { await repo.CreateBookingAsync(1, request with { CarId = 8 }, default); check(false, "request key reused for other car"); }
            catch (PaymentApiException ex) { check(ex.StatusCode == 409, "booking request conflict detected"); }
            var booking = bookings[0];
            var attemptRequest = Guid.NewGuid();
            var attempts = await Task.WhenAll(repo.AcquireAttemptAsync(1, booking.Id, attemptRequest, default), repo.AcquireAttemptAsync(1, booking.Id, attemptRequest, default));
            check(attempts.Count(a => a.Created) == 1 && attempts[0].Payment.Id == attempts[1].Payment.Id, "concurrent payment idempotency");
            check(await Count(db, "payments") == 1, "only one payment persisted");
            var payment = attempts[0].Payment;
            check(!(await repo.AcquireAttemptAsync(1, booking.Id, Guid.NewGuid(), default)).Created, "different key cannot duplicate active attempt");
            try { await repo.GetAsync(payment.Id, 2, default); check(false, "foreign payment allowed"); }
            catch (PaymentApiException ex) { check(ex.StatusCode == 404, "foreign payment inaccessible in SQL"); }
            check((await repo.HistoryAsync(2, 0, 100, default)).Count == 0, "history isolates user");
            await repo.SaveCheckoutAsync(payment.Id, "synthetic-encrypted-payload", ServerConfiguration.Hash("synthetic-token"), DateTime.UtcNow.AddMinutes(30), default);
            check((await repo.FindTokenAsync(ServerConfiguration.Hash("synthetic-token"), default))?.Id == payment.Id, "hash callback lookup");
            await repo.RequestCancelAsync(payment.Id, 2, default);
            check(!(await repo.GetAsync(payment.Id, 1, default)).CancelRequested, "foreign cancel has no effect");
            var succeeded = await repo.ApplyAsync(payment.Id, new(PaymentStatus.Succeeded, "12345", "67890"), default);
            check(succeeded.Status == PaymentStatus.Succeeded && succeeded.Booking.Status == "Confirmed", "atomic payment and booking success");
            check((await repo.ApplyAsync(payment.Id, new(PaymentStatus.Review), default)).Status == PaymentStatus.Succeeded, "late unknown result cannot downgrade success");
            check(!(await repo.AcquireAttemptAsync(1, booking.Id, Guid.NewGuid(), default)).Created, "paid booking cannot be charged again");
            var secondBooking = await repo.CreateBookingAsync(1, request with { RequestId = Guid.NewGuid() }, default);
            var failed = await repo.AcquireAttemptAsync(1, secondBooking.Id, Guid.NewGuid(), default);
            await repo.ApplyAsync(failed.Payment.Id, new(PaymentStatus.Failed), default);
            var retry = await repo.AcquireAttemptAsync(1, secondBooking.Id, Guid.NewGuid(), default);
            check(retry.Created && retry.Payment.Booking.Id == secondBooking.Id, "retry uses the same booking after definite failure");
            try { await repo.ApplyAsync(retry.Payment.Id, new(PaymentStatus.Succeeded, "12345", "99999"), default); check(false, "duplicate provider payment reference accepted"); }
            catch (MySqlException ex) when (ex.Number == 1062) { check(true, "provider reference uniqueness enforced"); }
            check((await repo.GetAsync(retry.Payment.Id, 1, default)).Status != PaymentStatus.Succeeded, "reference conflict transaction rolled back");
            await repo.RequestCancelAsync(retry.Payment.Id, 1, default);
            var cancelled = await repo.ApplyAsync(retry.Payment.Id, new(PaymentStatus.Failed), default);
            check(cancelled.Status == PaymentStatus.Cancelled && cancelled.Booking.Status == "PaymentCancelled", "verified failed requested cancellation consistent");
            check(await Count(db, "bookings") == 2 && await Count(db, "payments") == 3, "expected persistent counts in isolated database");
            check((await repo.HistoryAsync(1, 0, 100, default)).Count == 3, "history joins actual persisted payment rows");
        }
        finally
        {
            if (created)
            {
                // Delete only the exact checked, newly created GUID test schema, never the source database.
                if (name == source.Database || !Regex.IsMatch(name, "^carrental_stage2_test_[0-9a-f]{32}$"))
                    throw new InvalidOperationException("Refusing unsafe test cleanup.");
                await Execute(admin, "DROP DATABASE `" + name + "`");
            }
        }
        Console.WriteLine("Isolated MySQL checks completed; temporary test database removed. Existing application records were not modified.");
    }

    private static async Task Execute(MySqlConnection db, string sql)
    { await using var command = new MySqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Count(MySqlConnection db, string table)
    { await using var command = new MySqlCommand("SELECT COUNT(*) FROM `" + table + "`", db); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
}
