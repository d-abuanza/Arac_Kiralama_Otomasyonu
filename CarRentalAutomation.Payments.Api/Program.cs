using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CarRentalAutomation.Payments.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

if (args.Length == 1 && args[0] == "--configure-local")
    return await LocalDevelopmentSetup.ConfigureAsync(CancellationToken.None);

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5158");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
// Load the same ID used by Stage 1, automatically and only for local Development.
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.Configure<FormOptions>(o => { o.ValueLengthLimit = 2048; o.ValueCountLimit = 10; o.MultipartBodyLengthLimit = 8192; });
builder.Services.AddSingleton(new ServerConfiguration(builder.Configuration));
builder.Services.AddSingleton<IPaymentRepository, MySqlPaymentRepository>();
builder.Services.AddHttpClient<ICheckoutProvider, IyzicoCheckoutProvider>(c => c.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<PaymentWorkflow>();
builder.Services.AddAuthentication("PaymentAccess").AddScheme<AuthenticationSchemeOptions, PaymentAuthentication>("PaymentAccess", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This local Sandbox backend uses Windows DPAPI. Configure hosted key protection before deploying to another OS.");
var keyDirectory = Path.Combine(LocalDevelopmentSetup.LocalDirectory, "ServerKeys");
builder.Services.AddDataProtection().SetApplicationName("CarRentalAutomation.Payments")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)).ProtectKeysWithDpapi();
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    try { await next(context); }
    catch (PaymentApiException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    }
    catch (Exception exception)
    {
        var reference = Guid.NewGuid().ToString("N");
        app.Logger.LogWarning("Payment API operation failed. Type {Type}; reference {Reference}", exception.GetType().Name, reference);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception is BadHttpRequestException ? 400 : 503;
            await context.Response.WriteAsJsonAsync(new { error = "İşlem tamamlanamadı. Ödeme durumunu yeniden kontrol edin.", reference });
        }
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (ServerConfiguration config, IPaymentRepository repository, CancellationToken ct) =>
{
    var databaseReady = await repository.CheckSchemaAsync(ct);
    return Results.Json(new { mode = "Sandbox", configured = config.IsConfigured, databaseReady,
        missingConfiguration = config.Missing }, statusCode: config.IsConfigured && databaseReady ? 200 : 503);
});

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/payments", async (HttpContext context, IPaymentRepository repository, int? offset, int? limit, string? status, CancellationToken ct) =>
    Results.Ok(await repository.HistoryAsync(UserId(context), offset ?? 0, limit ?? 100, ct, status)));
api.MapGet("/payments/{id:long}", async (long id, HttpContext context, IPaymentRepository repository, CancellationToken ct) =>
    Results.Ok((await repository.GetAsync(id, UserId(context), ct)).View));
api.MapGet("/payments/{id:long}/checkout", async (long id, HttpContext context, PaymentWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.ResumeAsync(id, UserId(context), ct)));
api.MapPost("/bookings", async (CreateBookingRequest request, HttpContext context, ServerConfiguration config,
    IPaymentRepository repository, CancellationToken ct) =>
{
    config.RequireReady();
    return Results.Ok((await repository.CreateBookingAsync(UserId(context), request, ct)).View);
});
api.MapPost("/bookings/{id:long}/checkout", async (long id, StartCheckoutRequest request, HttpContext context,
    ServerConfiguration config, PaymentWorkflow workflow, CancellationToken ct) =>
{
    config.RequireReady();
    return Results.Ok(await workflow.StartAsync(UserId(context), id, request, context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1", ct));
});
api.MapPost("/payments/{id:long}/verify", async (long id, HttpContext context, ServerConfiguration config, PaymentWorkflow workflow, CancellationToken ct) =>
{
    config.RequireReady();
    return Results.Ok(await workflow.VerifyAsync(id, UserId(context), ct));
});
api.MapPost("/payments/{id:long}/cancel", async (long id, HttpContext context, ServerConfiguration config, PaymentWorkflow workflow, CancellationToken ct) =>
{
    config.RequireReady();
    return Results.Ok(await workflow.CancelAsync(id, UserId(context), ct));
});
app.MapPost(ServerConfiguration.CallbackPath, async (HttpContext context, ServerConfiguration config, PaymentWorkflow workflow, CancellationToken ct) =>
{
    config.RequireReady();
    if (!context.Request.HasFormContentType || context.Request.QueryString.HasValue)
        throw new PaymentApiException(400, "Geçersiz ödeme bildirimi.");
    var form = await context.Request.ReadFormAsync(ct);
    if (form["token"].Count != 1) throw new PaymentApiException(400, "Geçersiz ödeme bildirimi.");
    await workflow.HandleCallbackAsync(form["token"].ToString(), ct);
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    return Results.Content("<!doctype html><html lang='tr'><meta charset='utf-8'><title>Sandbox ödeme</title><body>Ödeme bildirimi alındı. Güncel sonucu uygulamadan kontrol edin.</body></html>", "text/html; charset=utf-8");
});

await app.RunAsync();
return 0;

static long UserId(HttpContext context) => long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

public partial class Program;
