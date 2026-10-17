using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;

namespace CarRentalAutomation.WinForms;

internal sealed class PaymentsPage : UserControl
{
    private readonly IRentalPaymentApi? _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGridView _grid = new() { Name = "gridPayments", Dock = DockStyle.Fill,
        ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8), Text = "Ödeme geçmişini görmek için Yenile düğmesine basın." };
    private readonly Button _refresh = new() { Text = "Yenile", AutoSize = true };
    private readonly Button _previous = new() { Text = "Önceki", AutoSize = true, Enabled = false };
    private readonly Button _next = new() { Text = "Sonraki", AutoSize = true, Enabled = false };
    private int _offset;
    private int _count;
    private bool _busy;

    public PaymentsPage(IRentalPaymentApi? api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        AddColumn(nameof(PaymentRow.Id), "Ödeme No");
        AddColumn(nameof(PaymentRow.BookingId), "Rezervasyon No");
        AddColumn(nameof(PaymentRow.Model), "Araç Modeli");
        AddColumn(nameof(PaymentRow.Amount), "Tutar", "N2");
        AddColumn(nameof(PaymentRow.Currency), "Para Birimi");
        AddColumn(nameof(PaymentRow.Provider), "Ödeme Sağlayıcısı");
        AddColumn(nameof(PaymentRow.Status), "Ödeme Durumu");
        AddColumn(nameof(PaymentRow.Kind), "İşlem Türü");
        AddColumn(nameof(PaymentRow.CreatedAt), "İşlem Tarihi", "dd.MM.yyyy HH:mm");
        AddColumn(nameof(PaymentRow.Reference), "İşlem Referansı");
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        toolbar.Controls.AddRange([_refresh, _previous, _next]);
        Controls.Add(_grid);
        Controls.Add(_status);
        _status.Dock = DockStyle.Bottom;
        Controls.Add(toolbar);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _previous.Click += async (_, _) => await LoadAsync(Math.Max(0, _offset - 100));
        _next.Click += async (_, _) => await LoadAsync(_offset + 100);
    }

    private void AddColumn(string property, string title, string? format = null) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title,
            DefaultCellStyle = new DataGridViewCellStyle { Format = format ?? "" } });

    public Task RefreshAsync() => LoadAsync(_offset);

    private async Task LoadAsync(int offset)
    {
        if (_busy || IsDisposed) return;
        if (_api is null) { _status.Text = "Ödeme API bağlantısı yapılandırılmamış."; return; }
        _busy = true;
        UpdateButtons();
        _status.Text = "Ödeme geçmişi yükleniyor…";
        try
        {
            var records = await _api.HistoryAsync(offset, _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            // The API scopes records to its authenticated user; no client-side user ID is sent.
            _grid.DataSource = records.Select(payment => new PaymentRow(payment)).ToList();
            _offset = offset;
            _count = records.Count;
            _status.Text = records.Count == 0
                ? (offset == 0 ? "Henüz kayıtlı ödeme bulunmuyor." : "Bu sayfada başka ödeme yok. Önceki sayfaya dönebilirsiniz.")
                : $"{offset + 1}–{offset + records.Count} numaralı kayıtlar gösteriliyor. Her sayfada en fazla 100 ödeme.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (PaymentClientException exception)
        { if (!IsDisposed) _status.Text = "Ödemeler yüklenemedi: " + exception.Message + " Yenile ile tekrar deneyin."; }
        catch (Exception)
        { if (!IsDisposed) _status.Text = "Ödeme geçmişi yüklenemedi. API bağlantısını kontrol edip Yenile ile tekrar deneyin."; }
        finally { _busy = false; if (!IsDisposed) UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        _refresh.Enabled = !_busy;
        _previous.Enabled = !_busy && _offset > 0;
        _next.Enabled = !_busy && _count == 100 && _offset < 100000;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); }
        base.Dispose(disposing);
    }

    internal sealed class PaymentRow(RentalPayment payment)
    {
        public long Id => payment.Id;
        public long BookingId => payment.BookingId;
        public string Model => payment.Model;
        public decimal Amount => payment.Amount;
        public string Currency => payment.Currency;
        public string Provider => payment.Provider;
        public string Status => payment.Status == "SimulatedSuccess" ? "Eski simülasyon sonucu" : RentalPaymentStates.Label(payment.Status);
        public string Kind => payment.IsSimulation ? "Sandbox / Simülasyon" : "Gerçek";
        public DateTime CreatedAt => payment.CreatedAt;
        public string Reference => payment.ProviderReference ?? "—";
    }
}
