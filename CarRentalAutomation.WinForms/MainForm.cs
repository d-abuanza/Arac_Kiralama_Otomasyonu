using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;
using System.Text.RegularExpressions;

namespace CarRentalAutomation.WinForms;

public sealed class MainForm : Form
{
    private readonly ICollectionRepository _collections;
    private readonly IDatabaseConnectionCheck _database;
    private readonly ISiteCredentialRepository _siteCredentials;
    private readonly IBookingService _bookings = new MockBookingService();
    private readonly IPngReportExporter _reports = new PngReportExporter();
    private readonly Yolcu360Browser _browser = new();
    private readonly BackgroundBrowserHost _browserHost;
    private readonly long _userId;
    private readonly TextBox _location = new() { Name = "txtPickupLocation", AccessibleName = "Alış yeri", Text = "Istanbul Airport", Width = 190 };
    private readonly DateTimePicker _pickup = new() { Name = "dtpPickup", AccessibleName = "Alış tarihi ve saati", Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy HH:mm", Width = 150, Value = DateTime.Now.AddDays(7) };
    private readonly DateTimePicker _return = new() { Name = "dtpReturn", AccessibleName = "Dönüş tarihi ve saati", Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy HH:mm", Width = 150, Value = DateTime.Now.AddDays(10) };
    private readonly ComboBox _fuel = new() { Name = "cmbFuel", AccessibleName = "Yakıt filtresi", DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _transmission = new() { Name = "cmbTransmission", AccessibleName = "Vites filtresi", DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly DataGridView _grid = new() { Name = "gridOffers", AccessibleName = "Araç teklifleri", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly ListBox _history = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Name = "lblSearchStatus", AccessibleName = "Arama durumu", AutoSize = true, MaximumSize = new Size(1140, 0), Padding = new Padding(8) };
    private readonly Label _loginStatus = new() { Name = "lblLoginStatus", AutoSize = true, Text = "Yolcu360 tarayıcısı hazırlanıyor...", Padding = new Padding(8) };
    private readonly TextBox txtOtp = new() { Name = "txtOtp", Width = 110, MaxLength = 6, AccessibleName = "Altı haneli SMS doğrulama kodu" };
    private readonly Button btnVerifyOtp = new() { Name = "btnVerifyOtp", Text = "Doğrula / Giriş Yap", AutoSize = true, Enabled = false };
    private readonly Label _otpStatus = new() { Name = "lblOtpStatus", AutoSize = true, MaximumSize = new Size(620, 0), Padding = new Padding(8, 6, 0, 0) };
    private readonly Button _btnStartLogin = new() { Name = "btnStartLogin", Text = "Giriş işlemini başlat", AutoSize = true, Enabled = false };
    private readonly Button _btnSearch = new() { Name = "btnSearch", Text = "Ara", AutoSize = true, Padding = new Padding(5), Enabled = false };
    private readonly Button _btnSave = new() { Name = "btnSaveCollection", Text = "Koleksiyonu kaydet", AutoSize = true, Padding = new Padding(5), Enabled = false };
    private readonly Button _btnBook = new() { Name = "btnSimulateBooking", Text = "Rezervasyonu simule et", AutoSize = true, Padding = new Padding(5), Enabled = false };
    private readonly FlowLayoutPanel _otpPanel = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false, Padding = new Padding(8), WrapContents = true };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly BindingSource _binding = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private bool _browserReady;
    private bool _sessionCheckInProgress;
    private bool _loginInProgress;
    private bool _otpSubmitting;
    private bool _searchInProgress;
    private bool _closing;
    private bool _shutdownComplete;
    private bool _shutdownInProgress;
    private Task? _initializationTask;
    private bool _hasResultSet;
    private string _resultContext = "";
    private long _searchVersion;
    private SearchCriteria? _criteria;
    private List<CarOffer> _offers = [];
    private List<SavedCollection> _saved = [];

    public MainForm(ICollectionRepository collections, IDatabaseConnectionCheck database, ISiteCredentialRepository siteCredentials, long userId)
    {
        _collections = collections;
        _database = database;
        _siteCredentials = siteCredentials;
        _userId = userId;
        _browserHost = new BackgroundBrowserHost(_browser.Control);
        _browserHost.VerificationCancelled += CancelHumanVerification;
        _browser.StateChanged += BrowserStateChanged;
        _browser.HumanVerificationVisibilityChanged += HumanVerificationVisibilityChanged;
        Text = "CarRentalAutomation | Open Yazilim";
        Width = 1180; Height = 760; StartPosition = FormStartPosition.CenterScreen;
        _fuel.Items.AddRange(["Tümü", "Benzin", "Dizel", "Benzin/Dizel", "Hibrit", "Elektrik"]); _fuel.SelectedIndex = 0;
        _transmission.Items.AddRange(["Tümü", "Otomatik", "Manuel"]); _transmission.SelectedIndex = 0;
        _fuel.SelectedIndexChanged += (_, _) => ApplyFilters();
        _transmission.SelectedIndexChanged += (_, _) => ApplyFilters();
        ConfigureOfferGrid();
        BuildLayout();
    }

    private void ConfigureOfferGrid()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.Model), HeaderText = "Arac modeli", FillWeight = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.RentalCompany), HeaderText = "Kiralama sirketi", FillWeight = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.Transmission), HeaderText = "Vites", FillWeight = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.FuelType), HeaderText = "Yakit", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.Price), HeaderText = "Fiyat", DefaultCellStyle = new DataGridViewCellStyle { Format = "N2" }, FillWeight = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CarOffer.Currency), HeaderText = "Para birimi", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewLinkColumn { DataPropertyName = nameof(CarOffer.SourceUrl), HeaderText = "Teklif baglantisi", FillWeight = 130, TrackVisitedState = false });
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex] is not DataGridViewLinkColumn || _grid.Rows[e.RowIndex].DataBoundItem is not CarOffer { SourceUrl: { Length: > 0 } url }) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Baglanti acilamadi"); }
        };
        _grid.SelectionChanged += (_, _) => UpdateBrowserControls();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var searchPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        searchPanel.Controls.AddRange([new Label { Text = "Alis yeri", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _location,
            new Label { Text = "Alis", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _pickup,
            new Label { Text = "Donus", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _return,
            new Label { Text = "Yakit", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _fuel,
            new Label { Text = "Vites", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _transmission,
            _btnStartLogin, _btnSearch, _btnSave, _btnBook, _loginStatus]);
        _otpPanel.Controls.AddRange([new Label { Text = "SMS kodu (6 rakam):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, txtOtp, btnVerifyOtp, _otpStatus]);
        var resultsTab = new TabPage("Arac arama"); resultsTab.Controls.Add(_grid); _grid.DataSource = _binding;
        var historyTab = new TabPage("Gecmis kayitlar");
        var historyButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
        historyButtons.Controls.AddRange([Button("Yukle", LoadSelected), Button("Sil", DeleteSelected), Button("PNG disa aktar", ExportSelected)]);
        historyTab.Controls.Add(_history); historyTab.Controls.Add(historyButtons);
        _tabs.TabPages.Add(resultsTab); _tabs.TabPages.Add(historyTab);
        root.Controls.Add(searchPanel, 0, 0); root.Controls.Add(_otpPanel, 0, 1); root.Controls.Add(_tabs, 0, 2); root.Controls.Add(_status, 0, 3);
        Controls.Add(root);
        btnVerifyOtp.Click += VerifyOtpClicked;
        txtOtp.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9');
        txtOtp.TextChanged += (_, _) =>
        {
            var digits = new string(txtOtp.Text.Where(c => c is >= '0' and <= '9').Take(6).ToArray());
            if (txtOtp.Text != digits) { txtOtp.Text = digits; txtOtp.SelectionStart = digits.Length; }
            UpdateBrowserControls();
        };
        txtOtp.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (btnVerifyOtp.Enabled) btnVerifyOtp.PerformClick();
        };
        _btnStartLogin.Click += StartLoginClicked;
        _btnSearch.Click += SearchClicked;
        _btnSave.Click += SaveClicked;
        _btnBook.Click += BookClicked;
        FormClosing += CloseApplicationAsync;
        Shown += async (_, _) =>
        {
            _browserHost.StartInBackground(this);
            _initializationTask = InitializeSessionAsync();
            await _initializationTask;
        };
    }

    private static Button Button(string text, EventHandler handler)
    { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5) }; button.Click += handler; return button; }

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
        if (_closing || IsDisposed || !IsHandleCreated) return;
        void ApplyState()
        {
            if (_closing || IsDisposed) return;
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
        }
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)ApplyState); }
            catch (InvalidOperationException) when (_closing || IsDisposed) { }
        }
        else ApplyState();
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

    private async void SearchClicked(object? sender, EventArgs e)
    {
        if (_closing || !_browserReady || _sessionCheckInProgress || _loginInProgress || _otpSubmitting || _searchInProgress) return;
        if (_browser.State is not (Yolcu360SessionState.Authenticated or Yolcu360SessionState.SearchFailed))
        {
            _loginStatus.Text = "Önce Yolcu360 girişini ve SMS doğrulamasını tamamlayın.";
            return;
        }
        try
        {
            // Validate before clearing the current result set or changing any displayed dates.
            var input = new SearchCriteria(Regex.Replace(_location.Text, @"\s+", " ").Trim(), _pickup.Value, _return.Value);
            input.Validate();
            var criteria = new SearchCriteria(input.PickupLocation, RoundUpToHalfHour(input.PickupAt), RoundUpToHalfHour(input.ReturnAt));
            criteria.Validate();
            var adjustedTimes = criteria.PickupAt != input.PickupAt || criteria.ReturnAt != input.ReturnAt;
            _location.Text = criteria.PickupLocation;
            _pickup.Value = criteria.PickupAt;
            _return.Value = criteria.ReturnAt;
            _criteria = criteria;
            _searchInProgress = true;
            _searchVersion++;
            _hasResultSet = false;
            _resultContext = "";
            _offers = [];
            _binding.DataSource = Array.Empty<CarOffer>();
            _grid.ClearSelection();
            UpdateBrowserControls();
            _tabs.SelectedIndex = 0;
            var timeNote = adjustedTimes ? $"Site 30 dakika aralıklarını kullanır. Alış: {criteria.PickupAt:dd.MM.yyyy HH:mm}, dönüş: {criteria.ReturnAt:dd.MM.yyyy HH:mm}. " : "";
            SetBusy(true, timeNote + "Yolcu360 üzerinde yeni arama gönderiliyor...");
            var (result, offers) = await _browser.SearchAsync(criteria, _lifetimeCancellation.Token);
            if (_closing) return;
            _status.Text = timeNote + result.Message;
            if (!result.Succeeded) return;
            _offers = offers.ToList();
            _hasResultSet = true;
            _resultContext = timeNote + result.Message;
            ApplyFilters();
            _tabs.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) _status.Text = "Search could not run: " + ex.Message; }
        finally { _searchInProgress = false; if (!_closing) UseWaitCursor = false; UpdateBrowserControls(); }
    }

    private static DateTime RoundUpToHalfHour(DateTime value)
    {
        // The picker displays minutes; hidden seconds must not push an exact :00/:30 to the next slot.
        value = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
        var step = TimeSpan.TicksPerMinute * 30;
        var remainder = value.Ticks % step;
        return remainder == 0 ? value : value.AddTicks(step - remainder);
    }

    private void UpdateBrowserControls()
    {
        if (_closing || IsDisposed) return;
        var busy = _sessionCheckInProgress || _loginInProgress || _otpSubmitting || _searchInProgress;
        _btnStartLogin.Enabled = _browserReady && !busy;
        _btnSearch.Enabled = _browserReady && !busy && (_browser.State is Yolcu360SessionState.Authenticated or Yolcu360SessionState.SearchFailed);
        _location.Enabled = _pickup.Enabled = _return.Enabled = _fuel.Enabled = _transmission.Enabled = !_searchInProgress;
        _btnSave.Enabled = !_searchInProgress && _criteria is not null && _binding.Count > 0;
        _btnBook.Enabled = !_searchInProgress && _grid.CurrentRow?.DataBoundItem is CarOffer;
        var enableOtpInput = !busy && _browser.State == Yolcu360SessionState.WaitingForOtp;
        var focusOtpInput = enableOtpInput && !txtOtp.Enabled;
        txtOtp.Enabled = enableOtpInput;
        btnVerifyOtp.Enabled = txtOtp.Enabled && txtOtp.Text.Length == 6;
        if (focusOtpInput && _otpPanel.Visible) txtOtp.Focus();
    }

    private void ApplyFilters()
    {
        var filtered = OfferFilters.Apply(_offers, _fuel.SelectedItem?.ToString(), _transmission.SelectedItem?.ToString());
        _binding.DataSource = filtered;
        if (_hasResultSet)
        {
            var emptyMessage = filtered.Count == 0 ? (_offers.Count == 0 ? " Bu arama için uygun teklif bulunamadı." : " Seçilen yakıt ve vites filtrelerine uygun teklif yok.") : "";
            _status.Text = $"Gösterilen: {filtered.Count} / {_offers.Count} teklif.{emptyMessage} {_resultContext}".Trim();
        }
        UpdateBrowserControls();
    }

    private async void SaveClicked(object? sender, EventArgs e)
    {
        if (_closing || _searchInProgress) return;
        if (_criteria is null || _binding.Count == 0) { MessageBox.Show(this, "Search for offers first."); return; }
        var name = Microsoft.VisualBasic.Interaction.InputBox("Collection name:", "Save collection");
        if (_closing || string.IsNullOrWhiteSpace(name)) return;
        var token = _lifetimeCancellation.Token;
        try
        {
            var collectionCriteria = _criteria with
            {
                FuelFilter = _fuel.SelectedItem?.ToString() ?? "Tümü",
                TransmissionFilter = _transmission.SelectedItem?.ToString() ?? "Tümü"
            };
            await _collections.SaveAsync(name, collectionCriteria, ((IEnumerable<CarOffer>)_binding.List).ToArray(), token);
            if (_closing) return;
            await RefreshHistoryAsync();
            if (!_closing) _status.Text = "Collection saved.";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Save failed"); }
    }

    private async void BookClicked(object? sender, EventArgs e)
    {
        if (_closing || _searchInProgress) return;
        if (_grid.CurrentRow?.DataBoundItem is not CarOffer car) { MessageBox.Show(this, "Select an offer first."); return; }
        if (MessageBox.Show(this, $"Create a simulated booking for {car.Model}, {car.Price:N2} {car.Currency}?", "Simulation only", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var booking = await _bookings.CreateSimulatedBookingAsync(car);
        var payment = await _bookings.PayAsync(booking);
        _status.Text = $"Simulation only: booking #{booking.Id}; payment {payment.Status}. No real booking or payment was made.";
        MessageBox.Show(this, _status.Text, "Simulation");
    }

    private async Task RefreshHistoryAsync()
    {
        if (_closing) return;
        var saved = (await _collections.GetAllAsync(_lifetimeCancellation.Token)).ToList();
        if (_closing) return;
        _saved = saved; _history.DataSource = null; _history.DataSource = _saved; _history.DisplayMember = nameof(SavedCollection.Name);
    }
    private SavedCollection? SelectedCollection => _history.SelectedItem as SavedCollection;
    private async void LoadSelected(object? sender, EventArgs e)
    {
        if (_closing || _searchInProgress) return;
        if (SelectedCollection is not { } selected) return;
        var searchVersion = _searchVersion;
        var collection = selected;
        if (_collections is MySqlCollectionRepository repo)
        {
            try { collection = await repo.GetByIdAsync(selected.Id, _lifetimeCancellation.Token) ?? selected; }
            catch (OperationCanceledException) when (_closing) { return; }
            catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Load failed"); return; }
        }
        if (_closing || _searchInProgress || searchVersion != _searchVersion) return;
        _criteria = collection.Criteria; _offers = collection.Cars.ToList();
        _hasResultSet = true;
        _resultContext = $"'{collection.Name}' koleksiyonu yüklendi. Fiyatlar kaydedilmiş anlık görüntülerdir.";
        _location.Text = collection.Criteria.PickupLocation; _pickup.Value = collection.Criteria.PickupAt; _return.Value = collection.Criteria.ReturnAt;
        _fuel.SelectedItem = _fuel.Items.Contains(collection.Criteria.FuelFilter) ? collection.Criteria.FuelFilter : "Tümü";
        _transmission.SelectedItem = _transmission.Items.Contains(collection.Criteria.TransmissionFilter) ? collection.Criteria.TransmissionFilter : "Tümü";
        ApplyFilters();
    }
    private async void DeleteSelected(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (SelectedCollection is not { } collection) return;
        if (MessageBox.Show(this, $"Delete '{collection.Name}'?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (_closing) return;
        try { await _collections.DeleteAsync(collection.Id, _lifetimeCancellation.Token); if (!_closing) await RefreshHistoryAsync(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Delete failed"); }
    }
    private async void ExportSelected(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (SelectedCollection is not { } collection) return;
        try
        {
            if (_collections is MySqlCollectionRepository repo)
                collection = await repo.GetByIdAsync(collection.Id, _lifetimeCancellation.Token) ?? collection;
        }
        catch (OperationCanceledException) when (_closing) { return; }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Export failed"); return; }
        if (_closing) return;
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"{collection.Name}.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _reports.Export(collection, dialog.FileName); MessageBox.Show(this, "PNG report exported."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
    private void SetBusy(bool busy, string message) { UseWaitCursor = busy; _status.Text = message; }

    private void HumanVerificationVisibilityChanged(bool visible)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        void ApplyVisibility()
        {
            if (_closing || IsDisposed) return;
            if (visible) _browserHost.ShowVerification();
            else _browserHost.ReturnToBackground();
        }
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)ApplyVisibility); }
            catch (InvalidOperationException) when (_closing || IsDisposed) { }
        }
        else ApplyVisibility();
    }

    private void CancelHumanVerification()
    {
        if (_closing) return;
        _browser.CancelHumanVerification();
        _browserHost.ReturnToBackground();
        _loginStatus.Text = "Güvenlik doğrulaması iptal edildi. İşlemi yeniden başlatabilirsiniz.";
    }

    private async void CloseApplicationAsync(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        _closing = true;
        _lifetimeCancellation.Cancel();
        txtOtp.Clear();
        _btnStartLogin.Enabled = _btnSearch.Enabled = btnVerifyOtp.Enabled = _btnSave.Enabled = _btnBook.Enabled = false;
        _status.Text = "Uygulama kapatılıyor...";
        _browser.CancelHumanVerification();
        _browserHost.ReturnToBackground();
        try
        {
            if (_initializationTask is not null) await _initializationTask;
            await _browser.ShutdownAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // An interrupted WebView2 initialization must not leave the owned window/process alive.
            // Disposal below still runs, and no credential or verification values are logged.
        }
        finally
        {
            _browser.StateChanged -= BrowserStateChanged;
            _browser.HumanVerificationVisibilityChanged -= HumanVerificationVisibilityChanged;
            _browserHost.VerificationCancelled -= CancelHumanVerification;
            _browserHost.CloseAfterShutdown();
            _browser.Dispose();
            _lifetimeCancellation.Dispose();
            _shutdownComplete = true;
            Close();
        }
    }
}
