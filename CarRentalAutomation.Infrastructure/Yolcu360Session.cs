using System.Diagnostics;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

public sealed partial class Yolcu360Browser
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task? _initializationTask;
    private CancellationTokenSource? _humanVerificationCancellation;
    private bool _shutdownRequested;
    private bool _verificationVisible;
    private bool _serverRejectedSession;
    private int _sessionProbeStatus;
    private DateTime? _lastValidatedSessionUtc;
    private TimeSpan _humanVerificationTime;
    public event Action<bool>? HumanVerificationVisibilityChanged;

    public async Task<BrowserOperationResult> CheckSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdownRequested) return new BrowserOperationResult(State, "Uygulama kapatılıyor.");
        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return new BrowserOperationResult(State, "Bir tarayıcı işlemi devam ediyor.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        try
        {
            await InitializeAsync(linked.Token);
            return await CheckSessionCoreAsync(linked.Token);
        }
        catch (OperationCanceledException) { return SetState(Yolcu360SessionState.Cancelled, "Oturum kontrolü iptal edildi."); }
        catch (Exception ex) { return FailAuth("Oturum kontrolü tamamlanamadı: " + ex.Message); }
        finally { _operationLock.Release(); }
    }

    private async Task<BrowserOperationResult> CheckSessionCoreAsync(CancellationToken token)
    {
        SetState(Yolcu360SessionState.CheckingSession, "Yolcu360 oturumu kontrol ediliyor...");
        await EnsureSitePageAsync(token);
        var page = await ReadSettledSessionAsync(token);
        if (page.State == "authenticated") return await ValidateAuthenticatedSessionAsync(token);
        if (page.State == "otp") return SetState(Yolcu360SessionState.WaitingForOtp, "SMS doğrulaması bekleniyor. Kodu uygulamadaki alana girin; yeni SMS istenmedi.");
        if (page.State is "phone" or "signed-out" or "registration" or "expired")
            return FailAuth("Aktif Yolcu360 oturumu yok. Giriş işlemini başlat'a tıklayın.");
        return FailAuth("Yolcu360 oturum durumu doğrulanamadı. Giriş işlemini başlat'a tıklayarak tekrar kontrol edin.");
    }

    private async Task<LoginPageState> ReadSettledSessionAsync(CancellationToken token, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? PageTimeout);
        string? previous = null;
        DateTime? unknownSince = null;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            var page = await LoginDomAsync("state", token, phase: "ready");
            if (page.State == "challenge")
            {
                await ResolveHumanVerificationAsync(token);
                until = DateTime.UtcNow + (timeout ?? PageTimeout);
                previous = null;
                continue;
            }
            if (page.State is "authenticated" or "phone" or "otp" or "signed-out" or "registration" or "expired")
            {
                if (previous != page.State) { previous = page.State; stableSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(600)) return page;
            }
            else previous = null;
            if (page.State == "unknown")
            {
                unknownSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - unknownSince > TimeSpan.FromSeconds(4)) return page;
            }
            else unknownSince = null;
            await Task.Delay(200, token);
        }
        return new LoginPageState("timeout");
    }

    private Task EnsureSitePageAsync(CancellationToken token) => Control.Source is null || Control.Source.Scheme == "about"
        ? NavigateAsync("https://yolcu360.com/", token) : Task.CompletedTask;

    private async Task<BrowserOperationResult> ValidateAuthenticatedSessionAsync(CancellationToken token)
    {
        if (!_serverRejectedSession && _lastValidatedSessionUtc is { } checkedAt && DateTime.UtcNow - checkedAt < TimeSpan.FromMinutes(2))
            return SetState(Yolcu360SessionState.Authenticated, "Yolcu360 oturumu zaten aktif. Mevcut oturum kullanılıyor.", true);

        SetState(Yolcu360SessionState.CheckingSession, "Mevcut oturum Yolcu360 hesabı üzerinden doğrulanıyor...");
        _sessionProbeStatus = 0;
        // This is the site's normal account page. Its normal user-scoped request validates the session;
        // the application never calls private auth/payment APIs itself or reads their bodies/tokens.
        await NavigateAsync("https://yolcu360.com/account", token);
        var until = DateTime.UtcNow + PageTimeout;
        while (DateTime.UtcNow < until)
        {
            var page = await LoginDomAsync("state", token, phase: "ready");
            if (page.State == "challenge")
            {
                await ResolveHumanVerificationAsync(token);
                until = DateTime.UtcNow + PageTimeout;
                continue;
            }
            if (page.State is "phone" or "signed-out")
            { _serverRejectedSession = true; return FailAuth("Yolcu360 oturumu sona ermiş. Giriş işlemini başlat ile yeniden giriş yapın."); }
            if (_sessionProbeStatus == 401)
            { _serverRejectedSession = true; return FailAuth("Yolcu360 mevcut oturumu kabul etmedi. Yeniden giriş gerekiyor."); }
            if (_sessionProbeStatus >= 400)
                return FailAuth($"Yolcu360 oturum kontrolü isteğini kabul etmedi (HTTP {_sessionProbeStatus}). Giriş otomatik tekrarlanmadı.");
            if (page.State == "authenticated" && _sessionProbeStatus is >= 200 and < 300)
            {
                _serverRejectedSession = false;
                _lastValidatedSessionUtc = DateTime.UtcNow;
                return SetState(Yolcu360SessionState.Authenticated, "Yolcu360 oturumu aktif; sunucunun normal hesap isteği kabul edildi.", true);
            }
            await Task.Delay(200, token);
        }
        return FailAuth("Hesap sayfası açıldı ancak sunucudan oturum doğrulaması alınamadı. Oturum aktif olarak işaretlenmedi; tekrar kontrol edin.");
    }

    public void CancelHumanVerification() => _humanVerificationCancellation?.Cancel();

    private async Task ResolveHumanVerificationAsync(CancellationToken token)
    {
        // Managed widgets may clear themselves; only expose a challenge that remains present.
        await Task.Delay(350, token);
        if ((await LoginDomAsync("state", token, phase: "ready")).State != "challenge") return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeCancellation.Token);
        var humanWait = Stopwatch.StartNew();
        _humanVerificationCancellation = linked;
        var previousState = State;
        try
        {
            SetState(Yolcu360SessionState.WaitingForSecurityChallenge, "İnsan doğrulaması gerekiyor. Açılan Yolcu360 penceresindeki kontrolü tamamlayın; işlem otomatik devam edecek.");
            _verificationVisible = true;
            HumanVerificationVisibilityChanged?.Invoke(true);
            var until = DateTime.UtcNow + ChallengeTimeout;
            string? previous = null;
            var stableSince = DateTime.UtcNow;
            while (DateTime.UtcNow < until)
            {
                var page = await LoginDomAsync("state", linked.Token, phase: "ready");
                if (page.State is "authenticated" or "phone" or "otp" or "signed-out" or "registration" or "expired")
                {
                    if (previous != page.State) { previous = page.State; stableSince = DateTime.UtcNow; }
                    else if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500))
                    {
                        SetState(previousState, "İnsan doğrulaması tamamlandı. Önceki işlem devam ediyor...");
                        return;
                    }
                }
                else previous = null;
                await Task.Delay(200, linked.Token);
            }
            throw new TimeoutException("İnsan doğrulaması 3 dakika içinde tamamlanmadı. İşlem durduruldu.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new HumanVerificationCancelledException(); }
        finally
        {
            _humanVerificationTime += humanWait.Elapsed;
            _humanVerificationCancellation = null;
            if (_verificationVisible) { _verificationVisible = false; HumanVerificationVisibilityChanged?.Invoke(false); }
        }
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _shutdownRequested = true;
        _lifetimeCancellation.Cancel();
        CancelHumanVerification();
        if (_initializationTask is { } initializing)
        {
            try { await initializing; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Initialization error is already reported to the UI. */ }
        }
        // All browser cleanup (including OTP clearing) finishes before the host/control is disposed.
        await _operationLock.WaitAsync();
        _operationLock.Release();
    }

    private async Task DisposeAfterOperationsAsync()
    {
        await ShutdownAsync();
        DisposeControl();
    }

    private void DisposeControl()
    {
        if (_disposed) return;
        _disposed = true;
        Control.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed class SessionAlreadyAuthenticatedException : Exception { }
    private sealed class HumanVerificationCancelledException : OperationCanceledException { }
}
