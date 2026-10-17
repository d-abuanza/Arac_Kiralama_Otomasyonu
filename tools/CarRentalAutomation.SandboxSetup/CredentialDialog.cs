namespace CarRentalAutomation.SandboxSetup;

internal sealed class CredentialDialog : Form
{
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, MaxLength = 1024 };
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, MaxLength = 1024 };
    private readonly Button _check = new() { Text = "Kaydet ve kontrol et", AutoSize = true };
    private readonly TextBox _status = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = "Anahtarları Sandbox hesabınızdan buraya yapıştırın. Bu kontrol ödeme oluşturmaz." };
    private readonly CancellationTokenSource _closing = new();
    private bool _working;
    internal bool Verified { get; private set; }

    internal CredentialDialog()
    {
        Text = "iyzico Sandbox — Aşama 1";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 470);
        MinimumSize = new Size(650, 500);
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 55, 40, 40, 55, 45 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        var title = new Label { Text = "Yalnızca Sandbox. API Key ve Secret Key değerlerini sohbete veya appsettings.json dosyasına yazmayın.", AutoSize = true, MaximumSize = new Size(620, 0) };
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
        layout.Controls.Add(new Label { Text = "API Key", AutoSize = true }, 0, 1);
        layout.Controls.Add(_apiKey, 1, 1);
        layout.Controls.Add(new Label { Text = "Secret Key", AutoSize = true }, 0, 2);
        layout.Controls.Add(_secretKey, 1, 2);
        var storage = new Label { Text = "Yerel geliştirme: .NET User Secrets, kullanıcı profilinizde proje dışında tutulur. Bu geliştirme deposu şifreli değildir.", AutoSize = true, MaximumSize = new Size(620, 0) };
        layout.Controls.Add(storage, 0, 3); layout.SetColumnSpan(storage, 2);
        layout.Controls.Add(_check, 1, 4);
        layout.Controls.Add(_status, 0, 5); layout.SetColumnSpan(_status, 2);
        Controls.Add(layout);
        AcceptButton = _check;
        _check.Click += CheckClicked;
        FormClosing += (_, _) => _closing.Cancel();
        FormClosed += (_, _) => { _apiKey.Clear(); _secretKey.Clear(); _closing.Dispose(); };
    }

    private async void CheckClicked(object? sender, EventArgs e)
    {
        if (_working) return;
        Verified = false;
        var error = SandboxConfiguration.ValidatePair(_apiKey.Text, _secretKey.Text);
        if (error is not null) { _status.Text = error; return; }
        var credentials = new SandboxCredentials(_apiKey.Text, _secretKey.Text);
        _apiKey.Clear(); _secretKey.Clear();
        _working = true;
        _check.Enabled = _apiKey.Enabled = _secretKey.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Yapılandırma kaydediliyor; ardından Sandbox kimlik doğrulaması kontrol edilecek…";
        try
        {
            if (!await SandboxConfiguration.SaveUserSecretsAsync(credentials, _closing.Token))
            {
                if (!IsDisposed) _status.Text = "User Secrets kaydedilemedi. .NET SDK ve kullanıcı profili izinlerini kontrol edin; değerler günlüğe yazılmadı.";
                return;
            }
            var configuration = SandboxConfiguration.Load();
            if (!configuration.IsValid)
            {
                if (!IsDisposed) _status.Text = configuration.Message;
                Console.WriteLine(configuration.Message);
                return;
            }
            if (configuration.Credentials!.ApiKey != credentials.ApiKey || configuration.Credentials.SecretKey != credentials.SecretKey)
            {
                const string mismatch = "The loaded key pair does not match the values just saved. Check environment overrides and your User Secrets configuration; no provider request was sent.";
                if (!IsDisposed) _status.Text = mismatch;
                Console.WriteLine(mismatch);
                return;
            }
            Console.WriteLine("Configuration loaded from " + configuration.Source + ". Key values are not printed. Endpoint: " + SandboxConfiguration.BaseUrl);
            if (!IsDisposed) _status.Text = "Anahtarlar yüklendi. Ödeme oluşturmadan Sandbox BIN kontrolü yapılıyor…";
            using var client = SandboxAuthenticationProbe.CreateClient();
            var result = await new SandboxAuthenticationProbe(client).CheckAsync(configuration.Credentials!, _closing.Token);
            Program.PrintResult(result);
            if (!IsDisposed)
            {
                Verified = result.Succeeded;
                _status.ForeColor = result.Succeeded ? Color.DarkGreen : Color.DarkRed;
                _status.Text = result.Message + Environment.NewLine + "Reference: " + result.Reference;
            }
        }
        catch
        {
            if (!IsDisposed) _status.Text = "Kontrol tamamlanamadı. .NET SDK, ağ bağlantısı ve User Secrets erişimini kontrol edin. Anahtar değerleri yazdırılmadı.";
            Console.WriteLine("Local setup failed; no booking or payment was created. Secret values have not been printed.");
        }
        finally
        {
            _working = false;
            if (!IsDisposed) _check.Enabled = _apiKey.Enabled = _secretKey.Enabled = true;
        }
    }
}
