using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CarRentalAutomation.SandboxSetup;
using Microsoft.Extensions.Configuration;

// All keys, responses and requests here are synthetic. Child-process configuration
// fixtures override all provider variables and isolate APPDATA before calling Load.
// The persistence check uses a unique synthetic User Secrets ID and clears its
// values in finally. No real credential store, database or network is read.
internal static class Program
{
    private const string Api = "sandbox-synthetic-api-key";
    private const string Secret = "sandbox-synthetic-secret-key";
    private static readonly SandboxCredentials Credentials = new(Api, Secret);
    private static int _checks;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0].StartsWith("--load-fixture=", StringComparison.Ordinal))
            return CheckIsolatedLoadFixture(args[0]["--load-fixture=".Length..]);
        try
        {
            CheckConfiguration();
            CheckHiddenDialog();
            CheckDesktopIsolation();
            await CheckEnvironmentLoadAsync();
            await CheckIsolatedSecretsRoundtripAsync();
            CheckAuthorizationVector();
            await CheckCapturedRequestAsync();
            await CheckResponsesAsync();
            await CheckTransportErrorsAsync();
            Console.WriteLine($"Sandbox setup: {_checks} synthetic regression checks passed. No real credentials, network, database or payments were used.");
            return 0;
        }
        catch (Exception exception)
        {
            // Never dump exception data: a failed synthetic test follows the same
            // no-secret-output rule as the setup utility.
            Console.Error.WriteLine($"Sandbox setup regression failure after {_checks} checks ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static void CheckHiddenDialog()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new CredentialDialog();
                Check(!dialog.Visible && !dialog.Verified, "credential dialog constructed without display or authentication");
                var textBoxes = Descendants(dialog).OfType<TextBox>().ToArray();
                var fields = textBoxes.Where(field => !field.ReadOnly).ToArray();
                Check(fields.Length == 2, "separate API and secret fields");
                Check(fields.All(field => field.UseSystemPasswordChar), "both credential fields masked");
                Check(fields.All(field => field.ShortcutsEnabled && field.Enabled), "paste shortcuts enabled for both fields");
                Check(fields.All(field => field.MaxLength == 1024), "complete credentials fit bounded fields");
                Check(fields.All(field => !field.Multiline), "credential fields are single-line");
                Check(textBoxes.Count(field => field.ReadOnly && field.Multiline) == 1, "separate read-only status area");
                Check(dialog.AcceptButton is Button button && button.Enabled, "keyboard submit available");
                fields[0].Text = Api;
                fields[1].Text = Secret;
                Check(fields[0].Text == Api && fields[1].Text == Secret, "synthetic credentials remain distinct and unmodified");

                typeof(Form).GetMethod("OnFormClosing", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(dialog, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                typeof(Form).GetMethod("OnFormClosed", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(dialog, [new FormClosedEventArgs(CloseReason.UserClosing)]);
                Check(fields.All(field => field.Text.Length == 0), "credential fields cleared when dialog closes");
                Check(!dialog.Visible && !dialog.Verified, "lifecycle checks never display or authenticate");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(15)), "hidden STA dialog checks finish promptly");
        if (failure is not null) throw failure;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void CheckDesktopIsolation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CarRentalAutomation.WinForms", "CarRentalAutomation.WinForms.csproj")))
            directory = directory.Parent;
        Check(directory is not null, "repository metadata available");
        foreach (var name in new[] { "CarRentalAutomation.WinForms", "CarRentalAutomation.Infrastructure", "CarRentalAutomation.Core" })
        {
            var project = XDocument.Load(Path.Combine(directory!.FullName, name, name + ".csproj"));
            Check(!project.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Any(element => ((string?)element.Attribute("Include"))?.Contains("SandboxSetup", StringComparison.OrdinalIgnoreCase) == true),
                name + " does not reference credential setup utility");
            Check(!project.Descendants().Any(element => element.Name.LocalName == "UserSecretsId"), name + " has no payment User Secrets identity");
        }
    }

    private static async Task CheckEnvironmentLoadAsync()
    {
        foreach (var mode in new[] { "valid", "partial", "production" })
        {
            var processPath = Environment.ProcessPath!;
            var start = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--load-fixture=" + mode);
            start.Environment["IYZICO_SANDBOX_API_KEY"] = Api;
            start.Environment["IYZICO_SANDBOX_SECRET_KEY"] = Secret;
            start.Environment["IYZICO_BASE_URL"] = SandboxConfiguration.BaseUrl;
            // The fixture never sees an actual user's secret store, even if Load
            // regresses into consulting storage before checking its environment.
            var isolatedProfile = Path.Combine(Path.GetTempPath(), "sandbox-setup-unused-profile-" + Guid.NewGuid().ToString("N"));
            start.Environment["APPDATA"] = isolatedProfile;
            start.Environment["DOTNET_USER_SECRETS_FALLBACK_DIR"] = isolatedProfile;
            if (mode == "partial") start.Environment.Remove("IYZICO_SANDBOX_SECRET_KEY");
            if (mode == "production") start.Environment["IYZICO_BASE_URL"] = "https://api.iyzipay.com";
            using var child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            var captured = await output + await errors;
            Check(child.ExitCode == 0, mode + " isolated Load fixture");
            NoSecrets(captured, mode + " isolated Load output redaction");
        }
    }

    private static int CheckIsolatedLoadFixture(string mode)
    {
        try
        {
            var result = SandboxConfiguration.Load();
            if (result.Source != "server environment") return 10;
            if (mode == "valid")
                return result.IsValid && result.Credentials?.ApiKey == Api && result.Credentials.SecretKey == Secret ? 0 : 11;
            return !result.IsValid && result.Credentials is null ? 0 : 12;
        }
        catch { return 13; }
    }

    private static async Task CheckIsolatedSecretsRoundtripAsync()
    {
        var testId = "CarRentalAutomation.SandboxSetup.Tests." + Guid.NewGuid().ToString("N");
        Check(testId != SandboxConfiguration.SecretsId && testId.StartsWith("CarRentalAutomation.SandboxSetup.Tests.", StringComparison.Ordinal),
            "synthetic persistence ID is distinct from production configuration");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Check(await SandboxConfiguration.SaveUserSecretsAsync(Credentials, timeout.Token, testId), "production save path accepts synthetic pair via stdin");
            var settings = new ConfigurationBuilder().AddUserSecrets(testId, reloadOnChange: false).Build();
            try
            {
                Check(settings["Iyzico:ApiKey"] == Api, "synthetic API key roundtrip exact without quoting, truncation or swap");
                Check(settings["Iyzico:SecretKey"] == Secret, "synthetic secret roundtrip exact without quoting, truncation or swap");
                Check(settings.AsEnumerable().Count(pair => pair.Value is not null) == 2, "only the synthetic pair persisted");
            }
            finally { (settings as IDisposable)?.Dispose(); }
        }
        finally
        {
            // Only this generated test identity may ever be cleared. CLI arguments
            // contain its ID, never either credential value or the real SecretsId.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "user-secrets", "clear", "--id", testId }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            var captured = await output + await errors;
            Check(child.ExitCode == 0, "synthetic User Secrets values cleared in finally");
            NoSecrets(captured, "synthetic cleanup output redaction");
            var cleared = new ConfigurationBuilder().AddUserSecrets(testId, reloadOnChange: false).Build();
            try { Check(!cleared.AsEnumerable().Any(pair => pair.Value is not null), "isolated test store is empty after cleanup"); }
            finally { (cleared as IDisposable)?.Dispose(); }
        }
    }

    private static void CheckConfiguration()
    {
        Check(SandboxConfiguration.BaseUrl == "https://sandbox-api.iyzipay.com", "fixed Sandbox origin");
        Check(SandboxConfiguration.ValidatePair(Api, Secret) is null, "synthetic valid pair");
        foreach (var pair in new (string? Api, string? Secret)[]
        {
            (null, null), (null, Secret), (Api, null), ("", Secret), (Api, ""), (" ", Secret), (Api, "\t")
        })
        {
            var error = SandboxConfiguration.ValidatePair(pair.Api, pair.Secret);
            Check(error is not null, "both keys required");
            NoSecrets(error!, "missing-pair validation");
        }

        foreach (var invalid in new[]
        {
            "production-example", "sandbox-", "SANDBOX-value", " sandbox-example", "sandbox-example ",
            "sandbox-example\r\n", "sandbox-example\t", "sandbox-\u00a0example", "sandbox-example\0",
            "\"sandbox-example\"", "'sandbox-example'", "sandbox-...example", "sandbox-\u2026example",
            "sandbox-CHANGE_ME", "sandbox-change_me", "sandbox-<API_KEY>", "sandbox->placeholder", new string('x', 1025)
        })
        {
            Check(SandboxConfiguration.ValidatePair(invalid, Secret) is not null, "invalid API format");
            Check(SandboxConfiguration.ValidatePair(Api, invalid) is not null, "invalid secret format");
        }
        Check(SandboxConfiguration.ValidatePair(Api, Api) is not null, "identical keys rejected");

        var saved = SandboxConfiguration.Resolve(Api, Secret, null, null, null);
        Check(saved.IsValid && saved.Source == ".NET User Secrets", "saved pair selected");
        Check(saved.Credentials?.ApiKey == Api && saved.Credentials.SecretKey == Secret, "saved pair preserved");
        NoSecrets(saved.ToString(), "configuration record redaction");
        NoSecrets(Credentials.ToString(), "credentials redaction");

        var environment = SandboxConfiguration.Resolve("sandbox-other-api", "sandbox-other-secret", Api, Secret, SandboxConfiguration.BaseUrl);
        Check(environment.IsValid && environment.Source == "server environment", "environment takes precedence");
        Check(environment.Credentials?.ApiKey == Api && environment.Credentials.SecretKey == Secret, "environment pair preserved");
        NoSecrets(environment.ToString(), "environment configuration redaction");
        Check(SandboxConfiguration.Resolve(null, null, Api, Secret, null).IsValid, "complete environment pair alone");
        Check(!SandboxConfiguration.Resolve(null, null, null, null, null).IsValid, "missing all keys rejected");
        Check(SandboxConfiguration.Resolve("invalid", "invalid", Api, Secret, null).IsValid, "invalid stored values ignored for environment pair");

        foreach (var pair in new (string? Api, string? Secret)[] { (Api, null), (null, Secret), ("", null), (null, ""), (Api, "") })
        {
            var result = SandboxConfiguration.Resolve(Api, Secret, pair.Api, pair.Secret, null);
            Check(!result.IsValid && result.Credentials is null, "partial environment never mixed with saved pair");
            Check(result.Source == "server environment", "partial environment source retained");
            NoSecrets(result.Message, "partial environment error redaction");
        }

        foreach (var invalidUrl in new[]
        {
            "https://api.iyzipay.com", "http://sandbox-api.iyzipay.com", "https://sandbox-api.iyzipay.com/",
            "https://sandbox-api.iyzipay.com/payment/bin/check", "https://sandbox-api.iyzipay.com?redirect=https://example.test",
            "https://sandbox-api.iyzipay.com#fragment", "https://user@sandbox-api.iyzipay.com", "https://sandbox-api.iyzipay.com:443",
            "https://sandbox-api.iyzipay.com.evil.test", "https://example.test", " https://sandbox-api.iyzipay.com",
            "https://sandbox-api.iyzipay.com ", "https://sandbox-api.iyzipay.com\r\n", "", Api
        })
        {
            var result = SandboxConfiguration.Resolve(Api, Secret, null, null, invalidUrl);
            Check(!result.IsValid && result.Credentials is null, "custom or nonexact URL rejected");
            NoSecrets(result.ToString(), "URL rejection redaction");
        }
    }

    private static void CheckAuthorizationVector()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        const string body = "{\"locale\":\"tr\",\"conversationId\":\"synthetic-vector\",\"binNumber\":\"55260800\"}";
        const string expectedSignature = "8c361be6949abd01b4815f134348993002abc3b52491e88220c68efac1cf10ea";
        const string expectedHeader = "IYZWSv2 YXBpS2V5OnNhbmRib3gtc3ludGhldGljLWFwaS1rZXkmcmFuZG9tS2V5OjAxMjM0NTY3ODlhYmNkZWYwMTIzNDU2Nzg5YWJjZGVmJnNpZ25hdHVyZTo4YzM2MWJlNjk0OWFiZDAxYjQ4MTVmMTM0MzQ4OTkzMDAyYWJjM2I1MjQ5MWU4ODIyMGM2OGVmYWMxY2YxMGVh";
        var actual = SandboxAuthenticationProbe.CreateAuthorization(Credentials, nonce, body);
        Check(actual == expectedHeader, "independent authorization vector");
        Check(DecodeAuthorization(actual)["signature"] == expectedSignature, "independent HMAC vector");
        Check(SandboxAuthenticationProbe.CreateAuthorization(Credentials, nonce, body + " ") != actual, "exact body whitespace affects signature");
        Check(SandboxAuthenticationProbe.CreateAuthorization(Credentials, nonce + "0", body) != actual, "nonce affects signature");
    }

    private static async Task CheckCapturedRequestAsync()
    {
        var handler = new RecordingHandler(request => JsonResponse(SuccessBody(request)));
        using var client = new HttpClient(handler);
        var probe = new SandboxAuthenticationProbe(client);
        var result = await probe.CheckAsync(Credentials);
        Check(result.Succeeded, "matching successful provider response accepted");
        NoSecrets(result.ToString(), "successful result redaction");
        Check(handler.Requests.Count == 1, "one nonpayment request");
        var request = handler.Requests.Single();
        Check(request.Method == HttpMethod.Post, "BIN uses POST");
        Check(request.Uri == new Uri("https://sandbox-api.iyzipay.com/payment/bin/check"), "request target fixed to HTTPS Sandbox BIN endpoint");
        Check(request.ContentType == "application/json; charset=utf-8", "JSON UTF-8 content type");
        Check(request.Accept == "application/json", "JSON response requested");
        Check(request.Nonce.Length == 32 && request.Nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "cryptographic nonce format");

        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Check(root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "binNumber", "conversationId", "locale" }), "only locale, conversation and BIN are sent");
        Check(root.GetProperty("locale").GetString() == "tr", "Turkish locale");
        Check(root.GetProperty("binNumber").GetString() == "55260800", "only synthetic eight-digit BIN");
        Check(SandboxAuthenticationProbe.TestBin.Length == 8 && SandboxAuthenticationProbe.TestBin.All(char.IsAsciiDigit), "BIN is eight digits, not a full card number");
        Check(root.GetProperty("conversationId").GetString() == result.Reference, "request correlation reference");
        Check(result.Reference.StartsWith("sandbox-check-", StringComparison.Ordinal), "safe reference prefix");
        Check(!request.Body.Contains(Api, StringComparison.Ordinal) && !request.Body.Contains(Secret, StringComparison.Ordinal), "keys excluded from request body");

        var decoded = DecodeAuthorization(request.Authorization);
        Check(decoded.Count == 3, "authorization has only documented fields");
        Check(decoded["apiKey"] == Api, "API key transmitted in authorization");
        Check(decoded["randomKey"] == request.Nonce, "authorization nonce matches x-iyzi-rnd");
        var prefix = Encoding.UTF8.GetBytes(request.Nonce + "/payment/bin/check");
        var signedBytes = prefix.Concat(request.BodyBytes).ToArray();
        using var independentHmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var expected = Convert.ToHexString(independentHmac.ComputeHash(signedBytes)).ToLowerInvariant();
        Check(decoded["signature"] == expected, "signature covers captured exact transmitted body bytes");
        Check(request.BodyBytes.SequenceEqual(Encoding.UTF8.GetBytes(request.Body)), "captured request bytes are exact UTF-8");
        Check(!request.Authorization.Contains(Secret, StringComparison.Ordinal), "secret never directly transmitted");

        await probe.CheckAsync(Credentials);
        Check(handler.Requests.Count == 2 && handler.Requests[1].Nonce != request.Nonce, "each attempt uses a fresh nonce");
        Check(handler.Requests[1].Body != request.Body, "each attempt uses a fresh conversation reference");
    }

    private static async Task CheckResponsesAsync()
    {
        var scenarios = new (string Name, Func<RecordedRequest, HttpResponseMessage> Response, string? ExpectedCode)[]
        {
            ("wrong conversation", r => JsonResponse(SuccessBody(r).Replace(Conversation(r), "other-reference", StringComparison.Ordinal)), null),
            ("wrong BIN", r => JsonResponse(SuccessBody(r).Replace("55260800", "12345678", StringComparison.Ordinal)), null),
            ("missing correlation", _ => JsonResponse("{\"status\":\"success\",\"binNumber\":\"55260800\"}"), null),
            ("missing BIN", r => JsonResponse(JsonSerializer.Serialize(new { status = "success", conversationId = Conversation(r) })), null),
            ("capitalized status", r => JsonResponse(SuccessBody(r).Replace("success", "Success", StringComparison.Ordinal)), null),
            ("success on HTTP failure", r => JsonResponse(SuccessBody(r), HttpStatusCode.InternalServerError), null),
            ("success on redirect", r => JsonResponse(SuccessBody(r), HttpStatusCode.Found), null),
            ("signature rejection", _ => JsonResponse("{\"status\":\"failure\",\"errorCode\":\"1000\",\"errorMessage\":\"" + Secret + "\"}"), "1000"),
            ("other provider code", _ => JsonResponse("{\"status\":\"failure\",\"errorCode\":\"1001\"}", HttpStatusCode.BadRequest), "1001"),
            ("malicious provider code", _ => JsonResponse("{\"status\":\"failure\",\"errorCode\":\"" + Api + "\"}"), null),
            ("oversized provider code", _ => JsonResponse("{\"errorCode\":\"12345678901\"}"), null),
            ("nonnumeric provider code", _ => JsonResponse("{\"errorCode\":\"1000error\"}"), null),
            ("nonASCII provider code", _ => JsonResponse("{\"errorCode\":\"\u0661\u0660\u0660\u0660\"}"), null),
            ("numeric provider code", _ => JsonResponse("{\"errorCode\":1000}"), null),
            ("missing status", _ => JsonResponse("{}"), null),
            ("invalid JSON", _ => JsonResponse("invalid json " + Secret), null),
            ("array response", _ => JsonResponse("[\"" + Secret + "\"]"), null),
            ("null response", _ => JsonResponse("null"), null),
            ("string response", _ => JsonResponse("\"" + Secret + "\""), null),
            ("wrong property types", _ => JsonResponse("{\"status\":true,\"conversationId\":123,\"binNumber\":55260800}"), null),
            ("empty response", _ => JsonResponse("", HttpStatusCode.NoContent), null),
            ("oversized response", _ => JsonResponse("{\"message\":\"" + new string('x', 70 * 1024) + Secret + "\"}"), null)
        };
        foreach (var scenario in scenarios)
        {
            var handler = new RecordingHandler(scenario.Response);
            using var client = new HttpClient(handler);
            var result = await new SandboxAuthenticationProbe(client).CheckAsync(Credentials);
            Check(!result.Succeeded, scenario.Name + " not authenticated");
            Check(result.ProviderCode == scenario.ExpectedCode, scenario.Name + " code handled");
            Check(!string.IsNullOrWhiteSpace(result.Message) && !string.IsNullOrWhiteSpace(result.Reference), scenario.Name + " clear result and correlation");
            NoSecrets(result.ToString(), scenario.Name + " no response leakage");
        }
    }

    private static async Task CheckTransportErrorsAsync()
    {
        foreach (var exception in new Exception[]
        {
            new HttpRequestException("synthetic transport error " + Secret),
            new IOException("synthetic I/O error " + Api),
            new TaskCanceledException("synthetic timeout " + Secret)
        })
        {
            using var client = new HttpClient(new RecordingHandler(_ => throw exception));
            var result = await new SandboxAuthenticationProbe(client).CheckAsync(Credentials);
            Check(!result.Succeeded, "transport exception never authenticates");
            NoSecrets(result.ToString(), "transport exception no leakage");
            if (exception is TaskCanceledException)
                Check(result.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase), "timeout explained");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelledClient = new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("cancelled requests must not reach handler")));
        var cancelled = await new SandboxAuthenticationProbe(cancelledClient).CheckAsync(Credentials, cancellation.Token);
        Check(!cancelled.Succeeded && cancelled.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase), "caller cancellation explained");
        NoSecrets(cancelled.ToString(), "cancellation no leakage");
    }

    private static string Conversation(RecordedRequest request)
    {
        using var json = JsonDocument.Parse(request.Body);
        return json.RootElement.GetProperty("conversationId").GetString()!;
    }

    private static string SuccessBody(RecordedRequest request) => JsonSerializer.Serialize(new
    {
        status = "success", conversationId = Conversation(request), binNumber = "55260800"
    });

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> DecodeAuthorization(string header)
    {
        Check(header.StartsWith("IYZWSv2 ", StringComparison.Ordinal), "documented authorization scheme");
        return Encoding.UTF8.GetString(Convert.FromBase64String(header[8..]))
            .Split('&').Select(pair => pair.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
    }

    private static void NoSecrets(string text, string label) =>
        Check(!text.Contains(Api, StringComparison.Ordinal) && !text.Contains(Secret, StringComparison.Ordinal), label);

    private static void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        Console.Error.WriteLine("FAILED: " + label);
        throw new InvalidOperationException("Synthetic assertion failed.");
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, byte[] BodyBytes, string Body,
        string Authorization, string Nonce, string ContentType, string Accept);

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var captured = new RecordedRequest(request.Method, request.RequestUri!, bytes, Encoding.UTF8.GetString(bytes),
                string.Join(",", request.Headers.GetValues("Authorization")), string.Join(",", request.Headers.GetValues("x-iyzi-rnd")),
                request.Content?.Headers.ContentType?.ToString() ?? "", string.Join(",", request.Headers.Accept));
            Requests.Add(captured);
            return response(captured);
        }
    }
}
