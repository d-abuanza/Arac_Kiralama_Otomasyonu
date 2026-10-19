using CarRentalAutomation.Core;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm
{
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
        var searchPanel = CreateSearchPanel();
        _otpPanel.Controls.AddRange([new Label { Text = "SMS kodu (6 rakam):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, txtOtp, btnVerifyOtp, _otpStatus]);
        var resultsTab = new TabPage("Arac arama"); resultsTab.Controls.Add(_grid); _grid.DataSource = _binding;
        var historyTab = CreateHistoryTab();
        _tabs.TabPages.Add(resultsTab); _tabs.TabPages.Add(historyTab);
        AddPaymentsTab();
        root.Controls.Add(searchPanel, 0, 0); root.Controls.Add(_otpPanel, 0, 1); root.Controls.Add(_tabs, 0, 2); root.Controls.Add(_status, 0, 3);
        Controls.Add(root);
        WireEvents();
    }

    private FlowLayoutPanel CreateSearchPanel()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        panel.Controls.AddRange([new Label { Text = "Alış yeri", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _location,
            new Label { Text = "Alis", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _pickup,
            new Label { Text = "Donus", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _return,
            new Label { Text = "Yakit", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _fuel,
            new Label { Text = "Vites", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _transmission,
            _btnStartLogin, _btnSearch, _btnSave, _btnBook, _loginStatus]);
        return panel;
    }

    private TabPage CreateHistoryTab()
    {
        var historyTab = new TabPage("Gecmis kayitlar");
        var historyButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
        historyButtons.Controls.AddRange([Button("Yukle", LoadSelected), Button("Sil", DeleteSelected), Button("PNG disa aktar", ExportSelected)]);
        historyTab.Controls.Add(_history); historyTab.Controls.Add(historyButtons);
        return historyTab;
    }

    private void AddPaymentsTab()
    {
        _paymentsPage = new PaymentsPage(_payments);
        var paymentsTab = new TabPage("Ödemeler") { Name = "tabPayments" };
        paymentsTab.Controls.Add(_paymentsPage);
        _tabs.TabPages.Add(paymentsTab);
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedTab == paymentsTab) await _paymentsPage.RefreshAsync();
        };
    }

    private void WireEvents()
    {
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
}
