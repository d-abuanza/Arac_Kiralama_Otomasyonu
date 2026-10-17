using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;
using CarRentalAutomation.WinForms;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--local-history" }))
            {
                using var api = new PaymentApiClient("http://127.0.0.1:5158");
                var records = api.HistoryAsync(0, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Production payment client loaded {records.Count} owned history records using the protected local credential. No booking or payment created.");
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            CheckUi();
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Rental client: {_checks} checks passed. No real credentials, network, database or payment used.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Rental client checks failed: " + exception.GetType().Name + ". " + (exception is InvalidOperationException ? exception.Message : "Details withheld."));
            return 1;
        }
    }
    private static void Check(bool condition, string label)
    { _checks++; if (!condition) throw new InvalidOperationException("Failed: " + label); }

    private static void CheckUi()
    {
        using var paymentsPage = new PaymentsPage(new FakeApi());
        var paymentGrid = Descendants(paymentsPage).OfType<DataGridView>().Single();
        Check(paymentGrid.Columns.Count == 10 && !paymentGrid.AutoGenerateColumns && paymentGrid.ReadOnly, "payment history exposes only ten explicit read-only columns");
        Check(!paymentGrid.Columns.Cast<DataGridViewColumn>().Any(column => column.DataPropertyName.Contains("Token") || column.DataPropertyName.Contains("Checkout")), "history has no checkout tokens or URLs");
        var paymentRow = new PaymentsPage.PaymentRow(Payment(1));
        Check(paymentRow.Kind == "Sandbox / Simülasyon" && paymentRow.Id == 1, "history preserves simulation flag and payment identity");
        using var buyer = new RentalBuyerDialog("Synthetic Car", 100m, "TRY", "05551112233");
        var fields = Descendants(buyer).OfType<TextBox>().ToArray();
        Check(fields.Length == 9 && fields.Single(f => f.Name == "buyer4").Text == "+905551112233", "database phone normalized for buyer only");
        Check(fields.Single(f => f.Name == "buyer0").Text == "", "buyer is not silently fabricated");
        buyer.FillSyntheticBuyer();
        typeof(RentalBuyerDialog).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(buyer, null);
        Check(buyer.Buyer is not null && buyer.Buyer.Name == "Sandbox", "explicit Sandbox fixture accepted");
        Check(!buyer.Buyer!.ToString().Contains("11111111111"), "buyer ToString redacted");
        foreach (var url in new[] { "http://sandbox-cpp.iyzipay.com?token=x", "https://cpp.iyzipay.com?token=x", "https://evil.test?token=x", "https://user@sandbox-cpp.iyzipay.com?token=x", "https://sandbox-cpp.iyzipay.com:444?token=x" })
            Check(!RentalCheckoutForm.SafeCheckoutUrl(url), "unsafe or live checkout rejected");
        Check(RentalCheckoutForm.SafeCheckoutUrl("https://sandbox-cpp.iyzipay.com?token=synthetic"), "Sandbox checkout allowed");
        Check(!RentalCheckoutForm.SafeNavigation("http://bank.example.test") && !RentalCheckoutForm.SafeNavigation("file:///C:/test") &&
            RentalCheckoutForm.SafeNavigation("https://bank.example.test/3ds"), "3DS HTTPS allowed, insecure navigation blocked");
        Check(!RentalPaymentStates.Succeeded("SimulatedSuccess") && !RentalPaymentStates.Succeeded("Pending"), "mock or pending cannot appear paid");
        using var checkout = new RentalCheckoutForm(new FakeApi(), new RentalPaymentCoordinator(new FakeApi(), new MemoryStore()), 7,
            new(Payment(1), "https://sandbox-cpp.iyzipay.com?token=synthetic"));
        var view = Descendants(checkout).OfType<Microsoft.Web.WebView2.WinForms.WebView2>().Single();
        Check(view.CreationProperties.IsInPrivateModeEnabled == true && view.CreationProperties.ProfileName.StartsWith("Sandbox"), "checkout private isolated profile configured before initialization");
    }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }

    private static async Task RunAsync()
    {
        const string credential = "synthetic-client-credential-with-more-than-32-characters";
        var requestId = Guid.NewGuid();
        var httpRequests = 0;
        using (var api = new PaymentApiClient("http://127.0.0.1:5158", new HttpClient(new Handler(async request =>
        {
            httpRequests++;
            Check(request.Headers.Authorization?.ToString() == "Bearer " + credential, "user access token used, not provider secret");
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("carId").GetInt64() == 7 && json.RootElement.GetProperty("requestId").GetGuid() == requestId, "booking ID/request preserved");
            Check(!body.Contains("userId") && !body.Contains("amount") && !body.Contains("currency") && !body.Contains(credential), "client cannot choose owner or price");
            return Response(new RentalBooking(11, "Synthetic", 100m, "TRY", "PendingPayment", DateTime.UtcNow));
        })), _ => Task.FromResult<string?>(credential)))
        { var booking = await api.CreateBookingAsync(7, requestId, default); Check(booking.Id == 11 && httpRequests == 1, "typed backend response"); }
        foreach (var address in new[] { "http://evil.test", "https://user@backend.example.test", "https://backend.example.test?token=x", "https://backend.example.test#fragment" })
            Check(!PaymentApiClient.ValidBaseAddress(address), "unsafe backend address rejected");
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.BadGateway, HttpStatusCode.Found })
        {
            using var api = new PaymentApiClient("http://localhost:5158", new HttpClient(new Handler(_ => Task.FromResult(Response(new { error = credential }, code)))), _ => Task.FromResult<string?>(credential));
            try { await api.GetAsync(1, default); Check(false, "HTTP error accepted"); }
            catch (PaymentClientException ex) { Check(ex.StatusCode == (int)code && !ex.Message.Contains(credential), "HTTP error sanitized without following redirect"); }
        }
        using (var api = new PaymentApiClient("http://localhost:5158", new HttpClient(new Handler(_ => throw new HttpRequestException("secret should not appear"))), _ => Task.FromResult<string?>(credential)))
        {
            try { await api.GetAsync(1, default); Check(false, "transport error accepted"); }
            catch (PaymentClientException ex) { Check(!ex.Message.Contains("secret should not appear"), "transport errors do not dump secrets"); }
        }
        var fake = new FakeApi { LoseBookingResponse = true };
        var store = new MemoryStore();
        var coordinator = new RentalPaymentCoordinator(fake, store);
        try { await coordinator.EnsureBookingAsync(7, default); Check(false, "lost booking response accepted"); }
        catch (PaymentClientException) { Check(store.Attempt?.BookingRequestId != Guid.Empty, "booking request persisted before network"); }
        var firstRequest = store.Attempt!.BookingRequestId;
        var bookingResult = await coordinator.EnsureBookingAsync(7, default);
        Check(fake.Bookings.Count == 1 && store.Attempt.BookingRequestId == firstRequest, "lost response reuses booking after recovery");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => coordinator.EnsureBookingAsync(7, default)));
        Check(fake.Bookings.Count == 1, "repeated booking calls cannot duplicate intent");
        var buyer = new RentalBuyer("Synthetic", "Buyer", "11111111111", "test@example.test", "+905551112233", "Synthetic Street", "Istanbul", "Turkey");
        fake.LosePaymentResponse = true;
        var paymentRequest = store.Attempt.PaymentRequestId;
        try { await coordinator.StartAsync(7, buyer, false, default); Check(false, "lost checkout response accepted"); }
        catch (PaymentClientException) { Check(store.Attempt.PaymentRequestId == paymentRequest && store.Attempt.PaymentId is null, "payment request retained after timeout"); }
        var checkout = await coordinator.StartAsync(7, buyer, false, default);
        Check(fake.Payments.Count == 1 && checkout.Payment.BookingId == bookingResult.Id, "checkout retry reuses same server payment");
        var restarted = new RentalPaymentCoordinator(fake, store);
        await restarted.EnsureBookingAsync(7, default);
        await restarted.StartAsync(7, buyer, false, default);
        Check(fake.Bookings.Count == 1 && fake.Payments.Count == 1, "coordinator restart preserves both request identities");
        try { await coordinator.StartAsync(7, buyer, true, default); Check(false, "pending payment retried"); }
        catch (InvalidOperationException) { Check(store.Attempt.PaymentRequestId == paymentRequest, "unknown/pending attempt blocks new payment ID"); }
        fake.SetStatus(checkout.Payment.Id, "Failed");
        var retried = await coordinator.StartAsync(7, buyer, true, default);
        Check(fake.Payments.Count == 2 && retried.Payment.BookingId == bookingResult.Id && store.Attempt.PaymentRequestId != paymentRequest, "definite failure retry keeps booking, uses new attempt");
        fake.SetStatus(retried.Payment.Id, "Succeeded");
        await coordinator.RecordAsync(7, await fake.GetAsync(retried.Payment.Id, default), default);
        Check(store.Attempt.LastStatus == "Succeeded", "verified status recorded without buyer or token");
        await coordinator.StartSeparateRentalAsync(7, default);
        await coordinator.EnsureBookingAsync(7, default);
        Check(fake.Bookings.Count == 2, "separate rental requires explicit reset after verified success");

        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "carrental-client-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(temp);
        try
        {
            var fileStore = new FileRentalAttemptStore("http://localhost:5158", 1, temp);
            var attempt = new RentalAttempt(7, Guid.NewGuid(), Guid.NewGuid(), 11, 22, "Pending");
            await fileStore.SaveAsync(attempt, default);
            Check(await new FileRentalAttemptStore("http://localhost:5158", 1, temp).ReadAsync(7, default) == attempt, "file journal survives store restart exactly");
            Check(await new FileRentalAttemptStore("http://localhost:5158", 2, temp).ReadAsync(7, default) is null, "journal scoped to user");
            var path = Directory.GetFiles(temp, "7.json", SearchOption.AllDirectories).Single();
            var content = await File.ReadAllTextAsync(path);
            Check(!content.Contains("Buyer") && !content.Contains("Token") && !content.Contains("Url") && !content.Contains("Email"), "journal has no buyer, token or checkout URL");
            await File.WriteAllTextAsync(path, "corrupt");
            try { await fileStore.ReadAsync(7, default); Check(false, "corrupt journal reset silently"); }
            catch (PaymentClientException) { Check(true, "corrupt journal fails closed"); }
        }
        finally
        {
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!temp.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temp).StartsWith("carrental-client-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup refused.");
            Directory.Delete(temp, recursive: true);
        }
    }
    internal static RentalPayment Payment(long id, long booking = 11, string status = "Pending") => new(id, booking, "Synthetic", 100m, "TRY", "iyzico", status, true, DateTime.UtcNow, null, false, DateTime.UtcNow.AddMinutes(30));
    private static HttpResponseMessage Response(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request); }
    private sealed class MemoryStore : IRentalAttemptStore
    {
        internal RentalAttempt? Attempt;
        public Task<RentalAttempt?> ReadAsync(long carId, CancellationToken ct) => Task.FromResult(Attempt);
        public Task SaveAsync(RentalAttempt attempt, CancellationToken ct) { Attempt = attempt; return Task.CompletedTask; }
    }
    private sealed class FakeApi : IRentalPaymentApi
    {
        internal Dictionary<Guid, RentalBooking> Bookings = [];
        internal Dictionary<Guid, RentalCheckout> Payments = [];
        internal bool LoseBookingResponse, LosePaymentResponse;
        public Task<RentalBooking> CreateBookingAsync(long carId, Guid requestId, CancellationToken ct)
        {
            if (!Bookings.TryGetValue(requestId, out var booking)) { booking = new(Bookings.Count + 11, "Synthetic", 100m, "TRY", "PendingPayment", DateTime.UtcNow); Bookings[requestId] = booking; }
            if (LoseBookingResponse) { LoseBookingResponse = false; throw new PaymentClientException("Synthetic lost response"); }
            return Task.FromResult(booking);
        }
        public Task<RentalCheckout> StartCheckoutAsync(long bookingId, Guid requestId, RentalBuyer buyer, CancellationToken ct)
        {
            if (!Payments.TryGetValue(requestId, out var checkout)) { checkout = new(Payment(Payments.Count + 22, bookingId), "https://sandbox-cpp.iyzipay.com?token=synthetic"); Payments[requestId] = checkout; }
            if (LosePaymentResponse) { LosePaymentResponse = false; throw new PaymentClientException("Synthetic lost response"); }
            return Task.FromResult(checkout);
        }
        public void SetStatus(long id, string status)
        { var entry = Payments.Single(pair => pair.Value.Payment.Id == id); Payments[entry.Key] = entry.Value with { Payment = entry.Value.Payment with { Status = status } }; }
        public Task<RentalPayment> GetAsync(long id, CancellationToken ct) => Task.FromResult(Payments.Values.Single(p => p.Payment.Id == id).Payment);
        public Task<RentalCheckout> ResumeAsync(long id, CancellationToken ct) => Task.FromResult(Payments.Values.Single(p => p.Payment.Id == id));
        public Task<RentalPayment> VerifyAsync(long id, CancellationToken ct) => GetAsync(id, ct);
        public Task<RentalPayment> CancelAsync(long id, CancellationToken ct) => GetAsync(id, ct);
        public Task<IReadOnlyList<RentalPayment>> HistoryAsync(int offset, CancellationToken ct) => Task.FromResult<IReadOnlyList<RentalPayment>>(Payments.Values.Select(p => p.Payment).ToList());
    }
}
