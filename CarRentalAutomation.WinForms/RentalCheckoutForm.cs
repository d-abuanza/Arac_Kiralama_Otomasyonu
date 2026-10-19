using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CarRentalAutomation.WinForms;

internal sealed class RentalCheckoutForm : Form
{
    private readonly IRentalPaymentApi _api;
    private readonly RentalPaymentCoordinator _coordinator;
    private readonly long _carId;
    private RentalBuyer? _buyer;
    private RentalCheckout _checkout;
    private readonly WebView2 _view;
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(1050, 0), Padding = new Padding(8) };
    private readonly Button _verify = new() { Text = "Ödeme durumunu kontrol et", AutoSize = true };
    private readonly Button _retry = new() { Text = "Aynı rezervasyon için yeniden dene", AutoSize = true };
    private readonly Button _fillTestCard = new() { Name = "btnFillSandboxCard", Text = "Test kartını doldur", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Form> _popups = [];
    private readonly HashSet<CoreWebView2> _configuredBrowsers = [];
    private bool _checking, _closing, _allowClose, _shown;
    private bool _resourcesDisposed;
    internal RentalPayment LatestPayment { get; private set; }
    internal string? LastError { get; private set; }

    internal RentalCheckoutForm(IRentalPaymentApi api, RentalPaymentCoordinator coordinator, long carId, RentalCheckout checkout, RentalBuyer? buyer = null)
    {
        _api = api; _coordinator = coordinator; _carId = carId; _checkout = checkout; _buyer = buyer;
        LatestPayment = checkout.Payment;
        Text = "iyzico Sandbox — ödeme";
        Width = 1080; Height = 830; StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        _view = new WebView2
        {
            Dock = DockStyle.Fill,
            Name = "sandboxCheckoutBrowser",
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarRentalAutomation", "Payments", "Browser"),
                ProfileName = "Sandbox" + Guid.NewGuid().ToString("N"),
                IsInPrivateModeEnabled = true
            }
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var close = new Button { Text = "Kapat / çıkış", AutoSize = true };
        close.Click += (_, _) => Close();
        actions.Controls.AddRange([_fillTestCard, _verify, _retry, close]);
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_view, 0, 1); layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        _verify.Click += async (_, _) => await RefreshAsync(verify: true);
        _retry.Click += RetryClicked;
        _fillTestCard.Click += async (_, _) => await FillTestCardAsync();
        _timer.Tick += async (_, _) => await RefreshAsync(verify: false);
        Shown += async (_, _) =>
        {
            _shown = true;
            ShowState();
            await NavigateCheckoutAsync();
            if (!_closing && !RentalPaymentStates.Terminal(LatestPayment.Status)) _timer.Start();
        };
        FormClosing += CloseAsync;
        ShowState();
    }

    private void ShowState()
    {
        _status.Text = $"Sandbox / kiralama simülasyonu — {LatestPayment.Model} — {LatestPayment.Amount:N2} {LatestPayment.Currency}\nRezervasyon #{LatestPayment.BookingId}, ödeme #{LatestPayment.Id}: {RentalPaymentStates.Label(LatestPayment.Status)}." +
            (LatestPayment.Status == "VerificationRequired" ? " Sonuç doğrulanmadan yeni ödeme başlatmayın." : "") +
            (LastError is null ? "" : "\n" + LastError);
        _retry.Enabled = !_checking && !_closing && RentalPaymentStates.Failed(LatestPayment.Status);
        _verify.Enabled = !_checking && !_closing;
        _fillTestCard.Enabled = !_closing && LatestPayment.IsSimulation && !RentalPaymentStates.Terminal(LatestPayment.Status);
        if (RentalPaymentStates.Terminal(LatestPayment.Status)) _timer.Stop();
    }

    private async Task FillTestCardAsync()
    {
        if (_closing || !LatestPayment.IsSimulation || RentalPaymentStates.Terminal(LatestPayment.Status)) return;
        var core = _view.CoreWebView2;
        if (core is null || !SafeCheckoutUrl(core.Source))
        { LastError = "Test kartı yalnızca yüklenmiş iyzico Sandbox ödeme formunda doldurulabilir."; ShowState(); return; }
        _fillTestCard.Enabled = false;
        try
        {
            var result = await core.ExecuteScriptAsync(FillTestCardScript);
            if (_closing || IsDisposed) return;
            LastError = result == "true"
                ? "Sandbox test kartı dolduruldu. Bilgileri kontrol edip Güvenli Öde düğmesine basın."
                : "Kart alanlarının tamamı bulunamadı veya değerler kabul edilmedi. Kart ile Öde bölümünü açıp tekrar deneyin; gerekirse test kartını elle girin.";
        }
        catch (Exception)
        { if (!_closing) LastError = "Test kartı doldurulamadı. Ödeme formunun yüklenmesini bekleyip tekrar deneyin."; }
        finally { if (!_closing && !IsDisposed) ShowState(); }
    }

    // Public Sandbox fixture only. Never reads or submits existing card values.
    internal const string FillTestCardScript = """
        (() => {
            if (location.protocol !== 'https:' || !['sandbox-cpp.iyzipay.com', 'sandbox-api.iyzipay.com'].includes(location.hostname)) return false;
            const visible = e => !e.disabled && !e.readOnly && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
            const inputs = [...document.querySelectorAll('input')].filter(visible);
            const key = e => [e.name, e.id, e.placeholder, e.getAttribute('aria-label'), e.autocomplete].join(' ').toLocaleLowerCase('tr-TR');
            const find = pattern => inputs.find(e => pattern.test(key(e)));
            const holder = find(/card.?holder|cc-name|kart üzerindeki|ad soyad/);
            const number = find(/card.?number|cc-number|kart numara/);
            const expiry = find(/expiry|expiration|expire.?date|cc-exp\b|ay\s*\/\s*yıl/);
            const month = find(/expire.?month|expiry.?month|cc-exp-month/);
            const year = find(/expire.?year|expiry.?year|cc-exp-year/);
            const cvc = find(/cvc|cvv|cc-csc|security.?code/);
            if (!holder || !number || !cvc || (!expiry && !(month && year))) return false;
            const set = (e, value) => {
                e.focus();
                Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(e, value);
                e.dispatchEvent(new Event('input', { bubbles: true }));
                e.dispatchEvent(new Event('change', { bubbles: true }));
                e.blur();
            };
            set(holder, 'Sandbox Test');
            set(number, '5526080000000006');
            if (month && year) { set(month, '11'); set(year, year.maxLength === 2 ? '28' : '2028'); }
            else set(expiry, '11/28');
            set(cvc, '123');
            const digits = e => e.value.replace(/\D/g, '');
            return holder.value === 'Sandbox Test' && digits(number) === '5526080000000006' && digits(cvc) === '123'
                && (month && year ? digits(month) === '11' && ['28', '2028'].includes(digits(year)) : digits(expiry) === '1128');
        })()
        """;

    private async Task NavigateCheckoutAsync()
    {
        if (_closing || _checkout.CheckoutUrl is null) return;
        if (!SafeCheckoutUrl(_checkout.CheckoutUrl)) { LastError = "Sandbox ödeme bağlantısı geçersiz. Sunucudaki durumu kontrol edin."; ShowState(); return; }
        try
        {
            await _view.EnsureCoreWebView2Async();
            if (_closing || IsDisposed) return;
            ConfigureBrowser(_view);
            _view.CoreWebView2.Navigate(_checkout.CheckoutUrl);
        }
        catch (Exception) { if (!_closing) { LastError = "Ödeme tarayıcısı açılamadı. WebView2 Runtime kurulumunu ve bağlantıyı kontrol edin; mevcut deneme korunmuştur."; ShowState(); } }
    }

    private void ConfigureBrowser(WebView2 view)
    {
        var core = view.CoreWebView2;
        if (!_configuredBrowsers.Add(core)) return;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.NavigationStarting += (_, args) =>
        {
            if (!SafeNavigation(args.Uri)) { args.Cancel = true; LastError = "Güvenli olmayan ödeme yönlendirmesi engellendi."; ShowState(); }
        };
        core.NavigationCompleted += async (_, args) =>
        {
            if (_closing) return;
            if (!args.IsSuccess) { LastError = "Ödeme sayfası yüklenemedi. Bağlantıyı ve sunucudaki ödeme durumunu kontrol edin."; ShowState(); }
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/api/payments/iyzico/callback")
                await RefreshAsync(verify: true);
        };
        core.NewWindowRequested += async (_, args) => await OpenCheckoutPopupAsync(args);
    }

    private async Task OpenCheckoutPopupAsync(CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_closing || !SafeNavigation(args.Uri)) return;
        using var deferral = args.GetDeferral();
        Form? popup = null;
        try
        {
            popup = new Form { Text = "Sandbox banka / 3D Secure doğrulaması", Width = 820, Height = 720, StartPosition = FormStartPosition.CenterParent };
            var popupView = new WebView2
            {
                Dock = DockStyle.Fill,
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = _view.CreationProperties.UserDataFolder,
                    ProfileName = _view.CreationProperties.ProfileName,
                    IsInPrivateModeEnabled = true
                }
            };
            popup.Controls.Add(popupView); _popups.Add(popup); popup.Show(this);
            await popupView.EnsureCoreWebView2Async(_view.CoreWebView2.Environment);
            if (_closing || popup.IsDisposed) return;
            ConfigureBrowser(popupView);
            args.NewWindow = popupView.CoreWebView2;
            popupView.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close();
            popup.FormClosed += async (_, _) => { _popups.Remove(popup); if (!_closing) await RefreshAsync(verify: true); };
        }
        catch (Exception)
        { popup?.Dispose(); if (!_closing) { LastError = "Banka doğrulama penceresi açılamadı. Ödeme sonucunu kontrol edin."; ShowState(); } }
    }

    private async Task RefreshAsync(bool verify)
    {
        if (_checking || _closing || IsDisposed) return;
        _checking = true; ShowState();
        try
        {
            var payment = verify ? await _api.VerifyAsync(LatestPayment.Id, _lifetime.Token) : await _api.GetAsync(LatestPayment.Id, _lifetime.Token);
            if (_closing) return;
            ValidatePayment(payment);
            LatestPayment = payment; LastError = null;
            await _coordinator.RecordAsync(_carId, payment, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (PaymentClientException exception) { if (!_closing) LastError = exception.Message; }
        catch (Exception) { if (!_closing) LastError = "Ödeme sonucu alınamadı veya yerel devam kaydı yazılamadı. Mevcut rezervasyon korunmuştur."; }
        finally { _checking = false; if (!_closing && !IsDisposed) ShowState(); }
    }

    private async void RetryClicked(object? sender, EventArgs e)
    {
        if (_checking || _closing || !RentalPaymentStates.Failed(LatestPayment.Status)) return;
        using var buyerDialog = new RentalBuyerDialog(LatestPayment.Model, LatestPayment.Amount, LatestPayment.Currency);
        if (buyerDialog.ShowDialog(this) != DialogResult.OK || buyerDialog.Buyer is null) return;
        _buyer = buyerDialog.Buyer;
        _checking = true; ShowState();
        try
        {
            _checkout = await _coordinator.StartAsync(_carId, _buyer, retryFailed: true, _lifetime.Token);
            LatestPayment = _checkout.Payment; LastError = null;
            await NavigateCheckoutAsync();
            if (!RentalPaymentStates.Terminal(LatestPayment.Status)) _timer.Start();
        }
        catch (PaymentClientException exception) { LastError = exception.Message; }
        catch (Exception) { LastError = "Yeni denemenin sonucu belirsiz. Bu rezervasyondan devam edin; ayrı kiralama oluşturmayın."; }
        finally { _checking = false; _buyer = null; if (!_closing && !IsDisposed) ShowState(); }
    }

    private async void CloseAsync(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || !_shown) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _timer.Stop(); _verify.Enabled = _retry.Enabled = false;
        _status.Text += "\nÇıkış öncesi ödeme sonucu kontrol ediliyor…";
        if (!RentalPaymentStates.Terminal(LatestPayment.Status))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var result = await _api.CancelAsync(LatestPayment.Id, timeout.Token);
                ValidatePayment(result); LatestPayment = result;
                await _coordinator.RecordAsync(_carId, result, timeout.Token);
            }
            catch (Exception) { LastError = "Çıkışta sonuç doğrulanamadı. Aynı araçtan devam ederek mevcut denemeyi kontrol edin; yeni ödeme oluşturmayın."; }
        }
        _lifetime.Cancel();
        _allowClose = true;
        Close();
    }

    private void ValidatePayment(RentalPayment payment)
    {
        if (payment.Id != LatestPayment.Id || payment.BookingId != LatestPayment.BookingId || payment.Amount != LatestPayment.Amount ||
            payment.Currency != LatestPayment.Currency || !payment.IsSimulation || payment.Provider != "iyzico")
            throw new InvalidOperationException("Ödeme yanıtı eşleşmedi.");
    }
    internal static bool SafeCheckoutUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host == "sandbox-cpp.iyzipay.com" || uri.Host == "sandbox-api.iyzipay.com" && uri.AbsolutePath.StartsWith("/checkoutform/", StringComparison.Ordinal));
    internal static bool SafeNavigation(string? value) => value == "about:blank" || Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true; _closing = true;
            _timer.Stop(); _timer.Dispose(); _lifetime.Cancel();
            foreach (var popup in _popups.ToArray()) popup.Dispose();
            _buyer = null; _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
