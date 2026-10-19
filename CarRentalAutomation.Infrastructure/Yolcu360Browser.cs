using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using CarRentalAutomation.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CarRentalAutomation.Infrastructure;

/// <summary>All Yolcu360 page discovery and interaction is centralized here. CAPTCHA is never automated.</summary>
public sealed partial class Yolcu360Browser : IDisposable
{
    public const string LoginUrl = "https://yolcu360.com/login";
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _disposed;
    private string? _navigationError;
    private ulong _latestNavigationId;
    private int _otpResponseStatus;
    public WebView2 Control { get; } = new() { Dock = DockStyle.Fill };
    public Yolcu360SessionState State { get; private set; } = Yolcu360SessionState.Initializing;
    public event Action<BrowserOperationResult>? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed || _shutdownRequested, this);
        _initializationTask ??= InitializeCoreAsync(_lifetimeCancellation.Token);
        return _initializationTask.WaitAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        SetState(Yolcu360SessionState.Initializing, "Yolcu360 arka plan tarayıcısı hazırlanıyor...");
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenYazilim", "CarRentalAutomation", "WebView2");
        Directory.CreateDirectory(userData);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await Control.EnsureCoreWebView2Async(environment);
        Control.CoreWebView2.NavigationStarting += (_, e) => { _latestNavigationId = e.NavigationId; _navigationError = null; };
        Control.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (e.NavigationId == _latestNavigationId)
                _navigationError = e.IsSuccess ? null : e.WebErrorStatus.ToString();
        };
        Control.CoreWebView2.WebResourceResponseReceived += (_, e) =>
        {
            // Observe only the status of the site's normal request. Never read/log OTP bodies, headers or tokens.
            if (e.Request.Method == "POST" && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                && uri.AbsolutePath == "/api/v1/accounts-api/auth/login/phone/")
                _otpResponseStatus = e.Response.StatusCode;
            if (e.Request.Method == "POST" && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var searchUri)
                && searchUri.Host is "yolcu360.com" or "www.yolcu360.com" or "api2.yolcu360.com"
                && searchUri.AbsolutePath == "/api/v1/search-api/search/point/")
            {
                _searchResponseStatus = e.Response.StatusCode;
                if (e.Response.StatusCode == 401) { _serverRejectedSession = true; _lastValidatedSessionUtc = null; }
            }
            if (e.Request.Method == "GET" && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var accountUri)
                && accountUri.Host is "yolcu360.com" or "www.yolcu360.com" or "api2.yolcu360.com"
                && Regex.IsMatch(accountUri.AbsolutePath, @"^/api/v1/payment-api/wallets/[^/]+/history/$"))
            {
                _sessionProbeStatus = e.Response.StatusCode;
                if (e.Response.StatusCode == 401) { _serverRejectedSession = true; _lastValidatedSessionUtc = null; }
            }
        };
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<BrowserOperationResult> AuthenticateAsync(string phoneNumber, CancellationToken cancellationToken = default) =>
        AuthenticateAsync(_ => Task.FromResult<string?>(phoneNumber), cancellationToken);

    public async Task<BrowserOperationResult> AuthenticateAsync(Func<CancellationToken, Task<string?>> phoneProvider, CancellationToken cancellationToken = default)
    {
        if (_shutdownRequested) return new BrowserOperationResult(State, "Uygulama kapatılıyor.");
        if (!await _operationLock.WaitAsync(0, cancellationToken)) return new BrowserOperationResult(State, "Bir tarayıcı işlemi devam ediyor.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var token = linked.Token;
        try
        {
            await InitializeAsync(token);
            await EnsureSitePageAsync(token);
            SetState(Yolcu360SessionState.CheckingSession, "Yolcu360 oturumu kontrol ediliyor...");
            var current = await ReadSettledSessionAsync(token);
            if (current.State == "authenticated")
            {
                var existing = await ValidateAuthenticatedSessionAsync(token);
                if (existing.Succeeded || !_serverRejectedSession) return existing;
            }
            if (current.State == "otp") return SetState(Yolcu360SessionState.WaitingForOtp, "SMS doğrulaması zaten bekleniyor. Yeni SMS gönderilmedi; kodu uygulamaya girin.");
            if (current.State == "timeout") return FailAuth("Sayfa başlangıç işlemleri tamamlanmadı. Giriş yeniden başlatılmadı; bağlantıyı kontrol edip tekrar deneyin.");
            var ready = current;
            if (current.State != "phone")
            {
                SetState(Yolcu360SessionState.LoadingLoginPage, "Yolcu360 giriş sayfası açılıyor...");
                await NavigateAsync(LoginUrl, token);
                SetState(Yolcu360SessionState.WaitingForPhoneField, "Telefon formunun hazır olması bekleniyor...");
                ready = await WaitForLoginStageAsync("ready", s => s is "phone" or "otp" or "authenticated", PageTimeout, token);
            }
            if (ready.State == "authenticated") return await ValidateAuthenticatedSessionAsync(token);
            if (ready.State == "otp") return SetState(Yolcu360SessionState.WaitingForOtp, "SMS kodunu uygulamadaki 6 haneli alana girin.");
            ThrowOnPageError(ready, false);
            if (ready.State != "phone") return FailAuth("Giriş sayfasındaki telefon alanı hazır değil. Sayfayı ve internet bağlantısını kontrol edin.");
            var phoneNumber = await phoneProvider(token);
            var nationalPhone = NormalizeTurkishPhoneNumber(phoneNumber ?? "");

            if (!await SubmitPhoneAsync(nationalPhone, token))
                return FailAuth("Telefon numarası sayfanın maskelemesi sırasında değişti. Gönderim yapılmadı.");
            var result = await WaitForLoginStageAsync("phone", s => s is "otp" or "authenticated", TimeSpan.FromSeconds(50), token);
            ThrowOnPageError(result, false);
            if (result.State == "authenticated") return await ValidateAuthenticatedSessionAsync(token);
            if (result.State == "otp") return SetState(Yolcu360SessionState.WaitingForOtp, "SMS doğrulama ekranı açıldı. Telefonunuza gelen 6 haneli kodu uygulamaya girin.");
            return FailAuth("Devam Et gönderildi ancak SMS doğrulama ekranı açılmadı. Sitedeki uyarıyı kontrol edin; yeniden SMS gönderilmedi.");
        }
        catch (SessionAlreadyAuthenticatedException) { return await ValidateAuthenticatedSessionAsync(token); }
        catch (OperationCanceledException) { return SetState(Yolcu360SessionState.Cancelled, "Giriş işlemi iptal edildi."); }
        catch (Exception ex) { return FailAuth("Giriş tamamlanamadı: " + ex.Message); }
        finally { _operationLock.Release(); }
    }

    private async Task<bool> SubmitPhoneAsync(string nationalPhone, CancellationToken token)
    {
        SetState(Yolcu360SessionState.WaitingForPhoneField, "Ülke seçicisi açılıyor: Türkiye (+90)...");
        await WaitForLoginActionAsync("open-country", s => s is "country-open" or "country-ready", "Sitedeki ülke seçicisi açılamadı.", token);
        await WaitForLoginActionAsync("select-country", s => s == "country-ready", "TR +90 seçeneği seçilemedi veya seçili ülke doğrulanamadı.", token);
        SetState(Yolcu360SessionState.SubmittingPhone, "Türkiye (+90) seçildi. MySQL telefon numarası forma yazılıyor...");
        await WaitForLoginActionAsync("fill-phone", s => s == "phone-filled", "Telefon alanı görünür ve düzenlenebilir değil.", token, phone: nationalPhone);
        await WaitForLoginActionAsync("check-phone", s => s == "phone-verified", "Formdaki telefon numarası veya +90 ülke kodu MySQL kaydıyla eşleşmiyor. Gönderim yapılmadı.", token, phone: nationalPhone);
        await Task.Delay(250, token);
        if ((await LoginDomAsync("check-phone", token, phone: nationalPhone)).State != "phone-verified")
            return false;
        SetState(Yolcu360SessionState.SubmittingPhone, "+90 ve telefon numarası doğrulandı. Devam Et düğmesine basılıyor...");
        await WaitForLoginActionAsync("submit-phone", s => s == "phone-submitted", "Devam Et düğmesi etkin değil veya telefon formu değişti. SMS gönderimi doğrulanamadı.", token, phone: nationalPhone);
        return true;
    }

    public async Task<BrowserOperationResult> VerifyOtpAsync(string otp, CancellationToken cancellationToken = default)
    {
        if (_shutdownRequested) return new BrowserOperationResult(State, "Uygulama kapatılıyor.");
        if (!Regex.IsMatch(otp ?? "", @"\A[0-9]{6}\z"))
            return SetState(Yolcu360SessionState.WaitingForOtp, "SMS ile gelen 6 haneli sayısal kodu girin.");
        if (!await _operationLock.WaitAsync(0, cancellationToken)) return new BrowserOperationResult(State, "Bir tarayıcı işlemi devam ediyor.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var token = linked.Token;
        try
        {
            await InitializeAsync(token);
            SetState(Yolcu360SessionState.VerifyingOtp, "SMS kodu Yolcu360 doğrulama alanına aktarılıyor...");
            var before = await LoginDomAsync("state", token, phase: "verify");
            var priorErrorKey = before.State == "invalid" ? before.ErrorKey ?? "" : null;
            if (before.State != "invalid") ThrowOnPageError(before, true);
            if (before.State == "authenticated") return await ValidateAuthenticatedSessionAsync(token);
            await WaitForLoginActionAsync("fill-otp", s => s == "otp-filled", "Sitedeki SMS doğrulama alanı bulunamadı veya düzenlenebilir değil.", token, otp: otp);
            await WaitForLoginActionAsync("check-otp", s => s == "otp-verified", "Kod web formuna doğru aktarılamadı. Gönderim yapılmadı.", token, otp: otp);
            SetState(Yolcu360SessionState.VerifyingOtp, "Kod, sitenin normal doğrulama düğmesiyle gönderiliyor...");
            _otpResponseStatus = 0;
            await WaitForLoginActionAsync("submit-otp", s => s == "otp-submitted", "Sitenin doğrulama düğmesi bulunamadı veya etkin değil.", token, otp: otp);
            await Task.Delay(750, token);
            var result = await WaitForLoginStageAsync("verify", s => s == "authenticated", PageTimeout, token, priorErrorKey);
            ThrowOnPageError(result, true);
            if (result.State == "authenticated") return await ValidateAuthenticatedSessionAsync(token);
            return SetState(Yolcu360SessionState.WaitingForOtp, "Yolcu360 giriş başarısını doğrulamadı. Kodun süresini ve sitedeki mesajı kontrol edin; gönderim otomatik tekrarlanmadı.");
        }
        catch (SessionAlreadyAuthenticatedException) { return await ValidateAuthenticatedSessionAsync(token); }
        catch (OperationCanceledException) { return SetState(Yolcu360SessionState.Cancelled, "SMS doğrulaması iptal edildi."); }
        catch (CodeExpiredException ex) { return FailAuth(ex.Message); }
        catch (Exception ex) { return SetState(Yolcu360SessionState.WaitingForOtp, "SMS doğrulaması tamamlanamadı: " + ex.Message); }
        finally
        {
            otp = string.Empty;
            if (!_disposed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await LoginDomAsync("clear-otp", cleanup.Token); }
                catch { /* Do not persist or log OTPs, including on page/navigation failures. */ }
            }
            _operationLock.Release();
        }
    }

    private static bool TryParseLocalizedPrice(string value, out decimal price)
    {
        var normalized = value.Trim();
        if (!Regex.IsMatch(normalized, @"^[0-9]+(?:[.,][0-9]+)*$")) { price = 0; return false; }
        var separator = Math.Max(normalized.LastIndexOf('.'), normalized.LastIndexOf(','));
        var decimals = separator >= 0 && normalized.Length - separator - 1 is 1 or 2;
        var integer = decimals ? normalized[..separator] : normalized;
        var fraction = decimals ? normalized[(separator + 1)..] : "";
        normalized = integer.Replace(".", "").Replace(",", "") + (decimals ? "." + fraction : "");
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out price);
    }

    private static string NormalizeCurrency(string value) => value.Trim().ToUpperInvariant() switch
    {
        "₺" or "TL" or "TRY" => "TRY",
        "€" => "EUR",
        "$" => "USD",
        "£" => "GBP",
        var currency => currency
    };

    private sealed record LoginPageState(string State, string? ErrorKey = null);
    private sealed class CodeExpiredException(string message) : Exception(message);

    private async Task<LoginPageState> LoginDomAsync(string action, CancellationToken token, string? phone = null, string? otp = null, string? phase = null)
    {
        var value = await EvaluateJsonAsync(Yolcu360LoginScript.Create(action, phone, otp, phase), token);
        if (value.GetProperty("state").GetString() == "unexpected-origin")
            throw new InvalidOperationException("Tarayıcı Yolcu360 alan adında değil. Giriş işlemini yeniden başlatın.");
        return new LoginPageState(value.GetProperty("state").GetString() ?? "unknown",
            value.TryGetProperty("errorKey", out var error) ? error.GetString() : null);
    }

    private async Task WaitForLoginActionAsync(string action, Func<string, bool> ready, string failure, CancellationToken token, string? phone = null, string? otp = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            token.ThrowIfCancellationRequested();
            if (_navigationError is { } networkError) throw new IOException("Web sayfası yüklenemedi (" + networkError + ").");
            var page = await LoginDomAsync("state", token, phase: "ready");
            if (page.State == "challenge")
            {
                await ResolveHumanVerificationAsync(token);
                timer.Restart();
                page = await LoginDomAsync("state", token, phase: "ready");
            }
            if (page.State == "authenticated") throw new SessionAlreadyAuthenticatedException();
            ThrowOnPageError(page, action.Contains("otp", StringComparison.Ordinal));
            var result = await LoginDomAsync(action, token, phone, otp);
            if (ready(result.State)) return;
            await Task.Delay(200, token);
        }
        throw new InvalidOperationException(failure);
    }

    private async Task<LoginPageState> WaitForLoginStageAsync(string phase, Func<string, bool> ready, TimeSpan timeout, CancellationToken token, string? priorErrorKey = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_navigationError is { } networkError) throw new IOException("Web sayfası yüklenemedi (" + networkError + "). İnternet bağlantısını kontrol edin.");
            var result = await LoginDomAsync("state", token, phase: phase);
            if (result.State == "challenge")
            {
                await ResolveHumanVerificationAsync(token);
                deadline = DateTime.UtcNow + timeout;
            }
            else
            {
                var staleError = result.State == "invalid" && priorErrorKey is not null && (result.ErrorKey ?? "") == priorErrorKey && _otpResponseStatus < 400;
                if (!staleError && (ready(result.State) || result.State is "invalid" or "expired" or "network-error" or "registration")) return result;
            }
            await Task.Delay(250, token);
        }
        return new LoginPageState("timeout");
    }

    private static void ThrowOnPageError(LoginPageState result, bool verifying)
    {
        if (result.State == "expired") throw new CodeExpiredException("SMS kodunun süresi doldu. Yeni kod istemek için Giriş işlemini başlat'a tıklayın.");
        if (result.State == "registration") throw new InvalidOperationException("Site üyelik bilgileri istiyor. Hesap girişi henüz tamamlanmadı; site panelindeki üyelik adımını kontrol edin.");
        if (result.State == "network-error") throw new IOException("Yolcu360 bir bağlantı/sunucu hatası gösterdi. İnternet bağlantısını kontrol edin.");
        if (result.State == "invalid") throw new InvalidOperationException(verifying
            ? "Yolcu360 SMS kodunu kabul etmedi. Güncel kodu kontrol ederek tekrar girin."
            : "Yolcu360 telefon isteğini kabul etmedi. Telefon kaydını ve site panelindeki uyarıyı kontrol edin.");
    }

    internal static string NormalizeTurkishPhoneNumber(string phoneNumber)
    {
        var digits = Regex.Replace(phoneNumber ?? "", @"[\s().+-]", "");
        if (!Regex.IsMatch(digits, @"^[0-9]+$")) throw new ArgumentException("SiteCredentials telefon kaydı eksik veya geçersiz karakterler içeriyor.");
        if (digits.StartsWith("0090", StringComparison.Ordinal)) digits = digits[4..];
        else if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        if (!Regex.IsMatch(digits, @"^5[0-9]{9}$")) throw new ArgumentException("SiteCredentials kaydı +90 5XXXXXXXXX, 905XXXXXXXXX, 05XXXXXXXXX veya 5XXXXXXXXX biçiminde bir Türkiye cep telefonu olmalıdır.");
        return digits;
    }

    private async Task NavigateAsync(string url, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (navigationId is null && e.Uri == url) navigationId = e.NavigationId;
            else if (navigationId is not null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var next)
                && next.Scheme == "https" && next.Host is "yolcu360.com" or "www.yolcu360.com") navigationId = e.NavigationId;
        }
        async void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId != navigationId) return;
            if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                await Task.Delay(250);
                if (e.NavigationId != navigationId) return;
            }
            if (e.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new IOException("Yolcu360 sayfası yüklenemedi (" + e.WebErrorStatus + ")."));
        }
        Control.CoreWebView2.NavigationStarting += OnStarting;
        Control.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            Control.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(PageTimeout, token);
            await WaitForDocumentAsync(PageTimeout, token);
        }
        finally
        {
            Control.CoreWebView2.NavigationStarting -= OnStarting;
            Control.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private async Task WaitForDocumentAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EvalStringAsync("document.readyState", cancellationToken) == "complete"
                && Control.Source?.Scheme == "https") return;
            if ((await LoginDomAsync("state", cancellationToken, phase: "ready")).State == "challenge")
            {
                await ResolveHumanVerificationAsync(cancellationToken);
                until = DateTime.UtcNow + timeout;
            }
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException("Yolcu360 page did not finish loading.");
    }

    private async Task<JsonElement> EvaluateJsonAsync(string script, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wrapped = "(() => { try { return { ok:true, value:(" + script + ") }; } catch(e) { return { ok:false, errorName:e.name }; } })()";
        var response = await Control.CoreWebView2.ExecuteScriptAsync(wrapped).WaitAsync(PageTimeout, token);
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok))
            throw new InvalidOperationException("Sayfa betiği sonuç döndürmedi. JavaScript sözdizimi veya sayfa değişikliği nedeniyle işlem durduruldu.");
        if (!ok.GetBoolean())
            throw new InvalidOperationException("Sayfa betiği çalışmadı (" + root.GetProperty("errorName").GetString() + "). Sayfanın yapısı değişmiş olabilir.");
        if (!root.TryGetProperty("value", out var value)) throw new InvalidOperationException("Sayfa betiği beklenen sonucu döndürmedi.");
        return value.Clone();
    }

    private async Task<string> EvalStringAsync(string script, CancellationToken token)
    {
        var value = await EvaluateJsonAsync(script, token);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }
    private BrowserOperationResult FailAuth(string message) => SetState(Yolcu360SessionState.AuthenticationFailed, message);
    private BrowserOperationResult SetState(Yolcu360SessionState state, string message, bool succeeded = false)
    {
        State = state;
        var result = new BrowserOperationResult(state, message, succeeded);
        StateChanged?.Invoke(result);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _shutdownRequested = true;
        _lifetimeCancellation.Cancel();
        if (_operationLock.CurrentCount == 0 || _initializationTask is { IsCompleted: false })
        { _ = DisposeAfterOperationsAsync(); return; }
        DisposeControl();
    }
}
