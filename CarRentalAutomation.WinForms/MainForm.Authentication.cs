using CarRentalAutomation.Core;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm
{
    private async Task InitializeSessionAsync()
    {
        try
        {
            _loginStatus.Text = "Connecting to MySQL...";
            var (success, message) = await _database.CheckAsync(_lifetimeCancellation.Token);
            _status.Text = message;
            if (!success) { _loginStatus.Text = "Database unavailable. Check connection settings."; return; }
            await RefreshHistoryAsync();
            _loginStatus.Text = "Initializing secure Yolcu360 browser...";
            await _browser.InitializeAsync(_lifetimeCancellation.Token);
            _browserReady = true;
            _sessionCheckInProgress = true;
            UpdateBrowserControls();
            var session = await _browser.CheckSessionAsync(_lifetimeCancellation.Token);
            if (!_closing) _loginStatus.Text = session.Message;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) _loginStatus.Text = "Startup failed: " + ex.Message; }
        finally { _sessionCheckInProgress = false; UpdateBrowserControls(); }
    }

    private async Task StartLoginAsync()
    {
        if (!_browserReady) { _loginStatus.Text = "Browser is not ready."; return; }
        if (_closing || _sessionCheckInProgress || _loginInProgress || _otpSubmitting || _searchInProgress) return;
        _loginInProgress = true;
        _otpPanel.Visible = false;
        txtOtp.Clear();
        UpdateBrowserControls();
        try
        {
            _loginStatus.Text = "Mevcut Yolcu360 oturumu kontrol ediliyor...";
            await _browser.AuthenticateAsync(token => _siteCredentials.GetPhoneNumberAsync(_userId, token), _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) _loginStatus.Text = "Login could not start: " + ex.Message; }
        finally { _loginInProgress = false; UpdateBrowserControls(); }
    }

    private async void StartLoginClicked(object? sender, EventArgs e) => await StartLoginAsync();

    private void BrowserStateChanged(BrowserOperationResult result)
    {
        DispatchBrowserUpdate(() =>
        {
            _loginStatus.Text = result.Message;
            _otpStatus.Text = result.Message;
            if (result.State == Yolcu360SessionState.WaitingForOtp)
            {
                _otpPanel.Visible = true;
                txtOtp.Clear();
                txtOtp.Focus();
            }
            if (result.State == Yolcu360SessionState.Authenticated)
            {
                _otpPanel.Visible = false;
                txtOtp.Clear();
                if (_offers.Count == 0 && !_searchInProgress && !_hasResultSet) _status.Text = "Yolcu360 session verified. Search is ready.";
            }
            if (result.State is Yolcu360SessionState.AuthenticationFailed or Yolcu360SessionState.Cancelled)
            {
                _otpPanel.Visible = false;
                txtOtp.Clear();
            }
            UpdateBrowserControls();
        });
    }

    private async void VerifyOtpClicked(object? sender, EventArgs e)
    {
        if (_closing || _sessionCheckInProgress || _loginInProgress || _otpSubmitting || _searchInProgress || _browser.State != Yolcu360SessionState.WaitingForOtp) return;
        var code = txtOtp.Text;
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9'))
        {
            _otpStatus.Text = "SMS ile gelen 6 haneli sayısal doğrulama kodunu girin.";
            txtOtp.Focus();
            return;
        }
        _otpSubmitting = true;
        txtOtp.Clear();
        UpdateBrowserControls();
        try { await _browser.VerifyOtpAsync(code, _lifetimeCancellation.Token); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception) { if (!_closing) _otpStatus.Text = "Kod doğrulanamadı. Durumu kontrol edip tekrar deneyin."; }
        finally { code = string.Empty; _otpSubmitting = false; UpdateBrowserControls(); }
    }

    private void HumanVerificationVisibilityChanged(bool visible)
    {
        DispatchBrowserUpdate(() =>
        {
            if (visible) _browserHost.ShowVerification();
            else _browserHost.ReturnToBackground();
        });
    }

    private void DispatchBrowserUpdate(Action update)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        void ApplyUpdate()
        {
            if (_closing || IsDisposed) return;
            update();
        }
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)ApplyUpdate); }
            catch (InvalidOperationException) when (_closing || IsDisposed) { }
        }
        else ApplyUpdate();
    }

    private void CancelHumanVerification()
    {
        if (_closing) return;
        _browser.CancelHumanVerification();
        _browserHost.ReturnToBackground();
        _loginStatus.Text = "Güvenlik doğrulaması iptal edildi. İşlemi yeniden başlatabilirsiniz.";
    }
}
