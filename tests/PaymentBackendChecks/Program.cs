using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarRentalAutomation.Payments.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Security.Claims;

var checks = 0;
const string apiKey = "sandbox-synthetic-api", secret = "sandbox-synthetic-secret", access = "synthetic-client-token-with-at-least-32-characters";
var values = new Dictionary<string, string?>
{
    ["ConnectionStrings:CarRental"] = "Server=synthetic.invalid;Database=synthetic;User ID=synthetic;",
    ["Iyzico:ApiKey"] = apiKey, ["Iyzico:SecretKey"] = secret,
    ["Payments:CallbackUrl"] = "https://callback.example.test/api/payments/iyzico/callback",
    ["Payments:Clients:0:UserId"] = "1", ["Payments:Clients:0:TokenHash"] = ServerConfiguration.Hash(access)
};
// This test runner uses synthetic configuration only. No User Secrets, real keys or database are read.
var savedEnvironment = new Dictionary<string, string?>();
foreach (var key in new[] { "IYZICO_SANDBOX_API_KEY", "IYZICO_SANDBOX_SECRET_KEY", "IYZICO_BASE_URL", "PAYMENTS_CALLBACK_URL", "CARRENTAL_CONNECTION_STRING" })
{
    savedEnvironment[key] = Environment.GetEnvironmentVariable(key);
    Environment.SetEnvironmentVariable(key, null);
}
try
{
    ServerConfiguration Config(Dictionary<string, string?>? input = null) => new(new ConfigurationBuilder().AddInMemoryCollection(input ?? values).Build());
    void Check(bool condition, string name) { checks++; if (!condition) throw new InvalidOperationException("Failed: " + name); }
    var config = Config();
    Check(config.IsConfigured && config.Clients.Single().UserId == 1, "valid synthetic setup and user binding");
    Check(!config.ToString().Contains(secret) && !config.ToString().Contains(apiKey), "configuration redacted");
    foreach (var callback in new[] { "http://callback.example.test/api/payments/iyzico/callback", "https://localhost/api/payments/iyzico/callback",
        "https://callback.example.test/wrong", "https://user@callback.example.test/api/payments/iyzico/callback", "https://callback.example.test/api/payments/iyzico/callback?token=x" })
        Check(!ServerConfiguration.ValidCallback(callback), "invalid callback rejected");
    Environment.SetEnvironmentVariable("IYZICO_BASE_URL", "https://api.iyzipay.com");
    Check(!Config().IsConfigured, "live mode rejected");
    Environment.SetEnvironmentVariable("IYZICO_BASE_URL", null);
    Environment.SetEnvironmentVariable("IYZICO_SANDBOX_API_KEY", "sandbox-other-pair");
    Check(!Config().IsConfigured, "partial environment pair cannot mix with saved secret");
    Environment.SetEnvironmentVariable("IYZICO_SANDBOX_API_KEY", null);
    var duplicateClients = new Dictionary<string, string?>(values) { ["Payments:Clients:1:UserId"] = "2", ["Payments:Clients:1:TokenHash"] = ServerConfiguration.Hash(access) };
    Check(!Config(duplicateClients).IsConfigured, "duplicate token binding rejected");
    Check(Config(duplicateClients).Clients.Count == 0, "ambiguous credentials disabled even for history reads");
    async Task<AuthenticateResult> Authenticate(string? header, ServerConfiguration? settings = null)
    {
        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers.Authorization = header;
        context.Request.Headers["X-User-Id"] = "2";
        context.Request.QueryString = new QueryString("?userId=2");
        var authenticator = new PaymentAuthentication(new StaticOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, settings ?? config);
        await authenticator.InitializeAsync(new AuthenticationScheme("PaymentAccess", null, typeof(PaymentAuthentication)), context);
        return await authenticator.AuthenticateAsync();
    }
    Check(!(await Authenticate(null)).Succeeded, "missing authentication rejected");
    Check(!(await Authenticate("Bearer invalid-token-with-at-least-32-characters")).Succeeded, "invalid authentication rejected");
    var authenticated = await Authenticate("Bearer " + access);
    Check(authenticated.Succeeded && authenticated.Principal!.FindFirstValue(ClaimTypes.NameIdentifier) == "1", "user comes from server token binding, not client header/query");
    Check((await Authenticate("bearer " + access)).Succeeded, "case-insensitive bearer scheme");
    Check(!(await Authenticate("Bearer " + access, Config(duplicateClients))).Succeeded, "duplicate binding cannot authenticate");

    var booking = new BookingRow(11, 1, 7, "Synthetic Car", 3476m, "TRY", "PendingPayment", true, DateTime.UtcNow);
    var payment = new PaymentRow(22, booking, 3476m, "TRY", "iyzico", "Initializing", true, DateTime.UtcNow, null, null, null, null, false);
    var buyer = new BuyerDetails("Synthetic", "Buyer", "11111111111", "synthetic@example.test", "+905551112233", "Synthetic Street", "Istanbul", "Turkey");
    buyer.Validate(); checks++;
    foreach (var invalid in new[] { buyer with { Email = "invalid" }, buyer with { PhoneNumber = "05551112233" }, buyer with { Address = "" }, buyer with { Name = new string('x', 81) } })
    {
        try { invalid.Validate(); Check(false, "invalid buyer accepted"); } catch (PaymentApiException ex) { Check(ex.StatusCode == 400, "invalid buyer rejected"); }
    }
    Check(!buyer.ToString().Contains("11111111111"), "PII redacted");
    foreach (var badUrl in new[] { "https://cpp.iyzipay.com?token=test", "http://sandbox-cpp.iyzipay.com?token=test", "https://sandbox-cpp.iyzipay.com.evil.test?token=test",
        "https://user@sandbox-cpp.iyzipay.com?token=test", "https://sandbox-cpp.iyzipay.com:444?token=test" })
        Check(!IyzicoCheckoutProvider.ValidCheckoutUrl(badUrl), "invalid checkout origin");
    Check(!IyzicoCheckoutProvider.CheckoutUrlMatchesToken("https://sandbox-cpp.iyzipay.com?token=other", "test"), "checkout token correlation");
    Check(IyzicoCheckoutProvider.Money(10.0100m) == "10.01" && IyzicoCheckoutProvider.Money(10m) == "10", "response signature decimal normalization");

    string Sign(params string[] parts) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(string.Join(':', parts)))).ToLowerInvariant();
    const string token = "synthetic-checkout-token";
    var session = new CheckoutState(token, "https://sandbox-cpp.iyzipay.com?token=" + token, DateTime.UtcNow.AddMinutes(30));
    var handler = new FakeHandler(async request =>
    {
        var body = await request.Content!.ReadAsStringAsync();
        Check(request.RequestUri!.AbsoluteUri == ServerConfiguration.SandboxUrl + IyzicoCheckoutProvider.InitializePath, "fixed Sandbox initialize endpoint");
        var nonce = request.Headers.GetValues("x-iyzi-rnd").Single();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.GetValues("Authorization").Single()[8..]));
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(nonce + request.RequestUri.AbsolutePath + body))).ToLowerInvariant();
        Check(decoded == "apiKey:" + apiKey + "&randomKey:" + nonce + "&signature:" + signature, "wire body and nonce are signed exactly");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Check(root.GetProperty("price").GetString() == "3476" && root.GetProperty("paidPrice").GetString() == "3476", "trusted rental-period total without day multiplication");
        Check(root.GetProperty("buyer").GetProperty("id").GetString() == "1", "buyer identity from authenticated booking");
        Check(root.GetProperty("enabledInstallments")[0].GetInt32() == 1, "single installment");
        Check(root.GetProperty("basketItems")[0].GetProperty("itemType").GetString() == "VIRTUAL", "rental simulation basket");
        Check(!body.Contains("cardNumber") && !body.Contains("cvc") && !body.Contains(secret), "no card or secret in payload");
        return Json(new { status = "success", conversationId = payment.ConversationId, token, paymentPageUrl = session.Url,
            tokenExpireTime = 1800, signature = Sign(payment.ConversationId, token) });
    });
    using var http = new HttpClient(handler);
    var initialized = await new IyzicoCheckoutProvider(http, config).InitializeAsync(payment, buyer, "127.0.0.1", default);
    Check(initialized.Token == token && initialized.Url == session.Url, "verified initialization result");
    Check(!initialized.ToString().Contains(token), "checkout state redacted");
    foreach (var corruption in new[] { "signature", "token", "url", "conversation" })
    {
        using var badHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(Json(new
        {
            status = "success", conversationId = corruption == "conversation" ? "other" : payment.ConversationId,
            token = corruption == "token" ? "other" : token,
            paymentPageUrl = corruption == "url" ? "https://sandbox-cpp.iyzipay.com?token=other" : session.Url,
            signature = corruption == "signature" ? new string('0', 64) : Sign(payment.ConversationId, token)
        }))));
        try { await new IyzicoCheckoutProvider(badHttp, config).InitializeAsync(payment, buyer, "127.0.0.1", default); Check(false, "bad init accepted"); }
        catch (PaymentApiException ex) { Check(ex.StatusCode == 502, "invalid initialized " + corruption + " rejected"); }
    }
    using (var declinedHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(Json(new { status = "failure", errorCode = "1000", errorMessage = secret })))))
    {
        try { await new IyzicoCheckoutProvider(declinedHttp, config).InitializeAsync(payment, buyer, "127.0.0.1", default); Check(false, "provider rejection accepted"); }
        catch (ProviderRejectedException ex) { Check(ex.Message.Contains("1000") && !ex.Message.Contains(secret), "provider error code retained without free-text leakage"); }
    }

    Dictionary<string, object?> Success() => new()
    {
        ["status"] = "success", ["paymentStatus"] = "SUCCESS", ["paymentId"] = "12345", ["conversationId"] = payment.ConversationId,
        ["token"] = token, ["basketId"] = payment.BasketId, ["currency"] = "TRY", ["price"] = 3476m, ["paidPrice"] = 3476m, ["fraudStatus"] = 1,
        ["itemTransactions"] = new[] { new { itemId = payment.ItemId, paymentTransactionId = "67890", price = 3476m, paidPrice = 3476m, transactionStatus = 2 } },
        ["signature"] = Sign("SUCCESS", "12345", "TRY", payment.BasketId, payment.ConversationId, "3476", "3476", token)
    };
    ProviderResult Validate(Dictionary<string, object?> response)
    { using var json = JsonDocument.Parse(JsonSerializer.Serialize(response)); return IyzicoCheckoutProvider.ValidateResult(payment, session, json.RootElement, secret); }
    Check(Validate(Success()).Status == PaymentStatus.Succeeded, "fully verified provider success");
    foreach (var (field, bad) in new (string, object?)[] { ("price", 1m), ("paidPrice", 1m), ("currency", "USD"), ("conversationId", "wrong"),
        ("token", "other-token"), ("basketId", "other-booking"), ("signature", new string('0', 64)), ("fraudStatus", 0), ("itemTransactions", Array.Empty<object>()), ("status", "failure") })
    { var response = Success(); response[field] = bad; Check(Validate(response).Status == PaymentStatus.Review, "unverified " + field + " cannot succeed"); }
    var failure = Success(); failure["paymentStatus"] = "FAILURE"; failure["signature"] = Sign("FAILURE", "12345", "TRY", payment.BasketId, payment.ConversationId, "3476", "3476", token);
    Check(Validate(failure).Status == PaymentStatus.Failed, "signed failure");
    var fraud = Success(); fraud["fraudStatus"] = -1;
    Check(Validate(fraud).Status == PaymentStatus.Failed, "rejected fraud not confirmed");
    using (var document = JsonDocument.Parse("null")) Check(IyzicoCheckoutProvider.ValidateResult(payment, session, document.RootElement, secret).Status == PaymentStatus.Review, "malformed result cannot confirm");

    var repository = new FakeRepository(payment);
    var provider = new FakeProvider(session);
    var protection = new EphemeralDataProtectionProvider();
    var workflow = new PaymentWorkflow(repository, provider, protection);
    var request = new StartCheckoutRequest(Guid.NewGuid(), buyer);
    var checkout = await workflow.StartAsync(1, booking.Id, request, "127.0.0.1", default);
    Check(checkout.CheckoutUrl == session.Url && checkout.Payment.Status == PaymentStatus.Pending, "protected session roundtrip");
    Check(repository.Current.ProtectedCheckout is not null && !repository.Current.ProtectedCheckout.Contains(token), "raw token not stored in plaintext");
    Check(repository.Current.TokenHash == ServerConfiguration.Hash(token), "token lookup uses hash");
    var resumed = await new PaymentWorkflow(repository, provider, protection).ResumeAsync(payment.Id, 1, default);
    Check(resumed.CheckoutUrl == session.Url && provider.Initializations == 1, "service restart can resume protected session without initialization");
    try { await workflow.ResumeAsync(payment.Id, 2, default); Check(false, "foreign session allowed"); }
    catch (PaymentApiException ex) { Check(ex.StatusCode == 404, "foreign session cannot be resumed"); }
    await workflow.StartAsync(1, booking.Id, request, "127.0.0.1", default);
    await workflow.StartAsync(1, booking.Id, request with { RequestId = Guid.NewGuid() }, "127.0.0.1", default);
    Check(provider.Initializations == 1, "active attempt reused across repeated clicks");
    try { await workflow.VerifyAsync(payment.Id, 2, default); Check(false, "foreign user allowed"); }
    catch (PaymentApiException ex) { Check(ex.StatusCode == 404, "foreign ownership rejected"); }
    provider.Outcome = new(PaymentStatus.Succeeded, "12345", "67890");
    var cancelled = await workflow.CancelAsync(payment.Id, 1, default);
    Check(cancelled.Status == PaymentStatus.Succeeded && cancelled.CancelRequested, "close/cancel cannot erase verified success");
    Check(cancelled.ProviderReference == "12345", "provider reference retained");
    await workflow.HandleCallbackAsync(token, default);
    Check(provider.Retrievals == 1, "duplicate callback cannot reprocess verified success");
    try { await workflow.HandleCallbackAsync("wrong-token", default); Check(false, "unknown callback token allowed"); }
    catch (PaymentApiException ex) { Check(ex.StatusCode == 404, "unknown callback rejected"); }
    Check(!JsonSerializer.Serialize(cancelled).Contains(token), "history DTO excludes checkout token");
    var uncertainRepo = new FakeRepository(payment);
    var uncertainProvider = new FakeProvider(session) { FailInitialization = true };
    var uncertainWorkflow = new PaymentWorkflow(uncertainRepo, uncertainProvider, new EphemeralDataProtectionProvider());
    try { await uncertainWorkflow.StartAsync(1, booking.Id, request, "127.0.0.1", default); Check(false, "timeout accepted"); }
    catch (PaymentApiException) { Check(uncertainRepo.Current.Status == PaymentStatus.Review, "ambiguous initialization blocks duplicate charge"); }
    await uncertainWorkflow.StartAsync(1, booking.Id, request with { RequestId = Guid.NewGuid() }, "127.0.0.1", default);
    Check(uncertainProvider.Initializations == 1, "ambiguous attempt not automatically reinitialized");
    var declinedRepository = new FakeRepository(payment);
    var declinedProvider = new FakeProvider(session) { RejectInitialization = true };
    try { await new PaymentWorkflow(declinedRepository, declinedProvider, protection).StartAsync(1, booking.Id, request, "127.0.0.1", default); Check(false, "declined init accepted"); }
    catch (PaymentApiException ex) { Check(ex.StatusCode == 502 && declinedRepository.Current.Status == PaymentStatus.Failed, "definite initialization failure persisted"); }
    if (args.Contains("--mysql-isolated")) await DatabaseChecks.RunAsync(Check);
    Console.WriteLine($"Payment backend: {checks} checks passed. No real payment or existing application records changed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Payment backend check failed: " + ex.GetType().Name + ". " + (ex is InvalidOperationException ? ex.Message : "Details withheld."));
    return 1;
}
finally { foreach (var entry in savedEnvironment) Environment.SetEnvironmentVariable(entry.Key, entry.Value); }

static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
}
sealed class FakeProvider(CheckoutState state) : ICheckoutProvider
{
    internal int Initializations, Retrievals;
    internal bool FailInitialization, RejectInitialization;
    internal ProviderResult Outcome = new(PaymentStatus.Review);
    public Task<CheckoutState> InitializeAsync(PaymentRow payment, BuyerDetails buyer, string ip, CancellationToken ct)
    { Initializations++; if (RejectInitialization) throw new ProviderRejectedException("Synthetic rejection"); if (FailInitialization) throw new HttpRequestException(); return Task.FromResult(state); }
    public Task<ProviderResult> RetrieveAsync(PaymentRow payment, CheckoutState checkout, CancellationToken ct)
    { Retrievals++; return Task.FromResult(Outcome); }
}
sealed class StaticOptions : IOptionsMonitor<AuthenticationSchemeOptions>
{
    public AuthenticationSchemeOptions CurrentValue { get; } = new();
    public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
}
sealed class FakeRepository(PaymentRow row) : IPaymentRepository
{
    internal PaymentRow Current = row;
    private bool _created;
    public Task<(PaymentRow Payment, bool Created)> AcquireAttemptAsync(long userId, long bookingId, Guid requestId, CancellationToken ct)
    { if (userId != Current.Booking.UserId || bookingId != Current.Booking.Id) throw new PaymentApiException(404, "Not found"); var created = !_created; _created = true; return Task.FromResult((Current, created)); }
    public Task<PaymentRow> GetAsync(long id, long? userId, CancellationToken ct)
    { if (id != Current.Id || userId.HasValue && userId != Current.Booking.UserId) throw new PaymentApiException(404, "Not found"); return Task.FromResult(Current); }
    public Task<PaymentRow?> FindTokenAsync(string hash, CancellationToken ct) => Task.FromResult(hash == Current.TokenHash ? Current : null);
    public Task SaveCheckoutAsync(long id, string state, string hash, DateTime expires, CancellationToken ct)
    { Current = Current with { Status = PaymentStatus.Pending, ProtectedCheckout = state, TokenHash = hash, ExpiresAt = expires }; return Task.CompletedTask; }
    public Task<PaymentRow> ApplyAsync(long id, ProviderResult result, CancellationToken ct)
    { if (!PaymentStatus.IsSuccess(Current.Status)) Current = Current with { Status = result.Status, ProviderReference = result.PaymentReference }; return Task.FromResult(Current); }
    public Task RequestCancelAsync(long id, long userId, CancellationToken ct) { Current = Current with { CancelRequested = true }; return Task.CompletedTask; }
    public Task<BookingRow> CreateBookingAsync(long userId, CreateBookingRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<PaymentView>> HistoryAsync(long userId, int offset, int limit, CancellationToken ct, string? status = null) => throw new NotSupportedException();
    public Task<bool> CheckSchemaAsync(CancellationToken ct) => Task.FromResult(true);
}
