using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm : Form
{
    private readonly ICollectionRepository _collections;
    private readonly IDatabaseConnectionCheck _database;
    private readonly ISiteCredentialRepository _siteCredentials;
    private readonly IPngReportExporter _reports = new PngReportExporter();
    private readonly Yolcu360Browser _browser = new();
    private readonly BackgroundBrowserHost _browserHost;
    private readonly long _userId;
    private readonly PickupLocationSelector _location = new();
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

    public MainForm(ICollectionRepository collections, IDatabaseConnectionCheck database, ISiteCredentialRepository siteCredentials, long userId,
        IRentalPaymentApi? payments = null, IRentalAttemptStore? paymentAttempts = null)
    {
        _collections = collections;
        _database = database;
        _siteCredentials = siteCredentials;
        _userId = userId;
        _payments = payments;
        _rentalPayments = payments is not null && paymentAttempts is not null ? new RentalPaymentCoordinator(payments, paymentAttempts) : null;
        _browserHost = new BackgroundBrowserHost(_browser.Control);
        _browserHost.VerificationCancelled += CancelHumanVerification;
        _browser.StateChanged += BrowserStateChanged;
        _browser.HumanVerificationVisibilityChanged += HumanVerificationVisibilityChanged;
        Text = "CarRentalAutomation";
        Width = 1180; Height = 760; StartPosition = FormStartPosition.CenterScreen;
        _fuel.Items.AddRange(["Tümü", "Benzin", "Dizel", "Benzin/Dizel", "Hibrit", "Elektrik"]); _fuel.SelectedIndex = 0;
        _transmission.Items.AddRange(["Tümü", "Otomatik", "Manuel"]); _transmission.SelectedIndex = 0;
        _fuel.SelectedIndexChanged += (_, _) => ApplyFilters();
        _transmission.SelectedIndexChanged += (_, _) => ApplyFilters();
        ConfigureOfferGrid();
        BuildLayout();
    }

    private void UpdateBrowserControls()
    {
        if (_closing || IsDisposed) return;
        var busy = _sessionCheckInProgress || _loginInProgress || _otpSubmitting || _searchInProgress || _paymentInProgress;
        _btnStartLogin.Enabled = _browserReady && !busy;
        _btnSearch.Enabled = _browserReady && !busy && (_browser.State is Yolcu360SessionState.Authenticated or Yolcu360SessionState.SearchFailed);
        _location.Enabled = _pickup.Enabled = _return.Enabled = _fuel.Enabled = _transmission.Enabled = !_searchInProgress && !_paymentInProgress;
        _btnSave.Enabled = !_searchInProgress && !_paymentInProgress && _criteria is not null && _binding.Count > 0;
        _btnBook.Enabled = !_searchInProgress && !_paymentInProgress && _grid.CurrentRow?.DataBoundItem is CarOffer && _rentalPayments is not null;
        var enableOtpInput = !busy && _browser.State == Yolcu360SessionState.WaitingForOtp;
        var focusOtpInput = enableOtpInput && !txtOtp.Enabled;
        txtOtp.Enabled = enableOtpInput;
        btnVerifyOtp.Enabled = txtOtp.Enabled && txtOtp.Text.Length == 6;
        if (focusOtpInput && _otpPanel.Visible) txtOtp.Focus();
    }

    private async void BookClicked(object? sender, EventArgs e)
    {
        if (_closing || _searchInProgress || _paymentInProgress) return;
        if (_grid.CurrentRow?.DataBoundItem is not CarOffer car) { MessageBox.Show(this, "Select an offer first."); return; }
        await RunRentalPaymentAsync(car);
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
