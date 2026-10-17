using Microsoft.Win32;

namespace CarRentalAutomation.WinForms;

/// <summary>
/// Keeps the one browser attached to a shown, normally sized window while its ordinary pages stay off screen.
/// Security verification moves this same window into view; it never reparents or hides the browser.
/// </summary>
internal sealed class BackgroundBrowserHost : Form
{
    private readonly Size _backgroundClientSize = new(1000, 700);
    private bool _verificationVisible;
    private bool _allowClose;
    private bool _placing;

    public event Action? VerificationCancelled;
    public bool IsShowingVerification => _verificationVisible;

    protected override bool ShowWithoutActivation => true;

    public BackgroundBrowserHost(Control browser)
    {
        if (browser.Parent is not null) throw new InvalidOperationException("The shared browser already has a host.");
        Name = "backgroundBrowserHost";
        Text = "Yolcu360 güvenlik doğrulaması";
        // Keep these styles fixed: changing them can recreate the native parent of WebView2.
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(640, 450);
        ClientSize = _backgroundClientSize;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        var cancel = new Button { Name = "btnCancelHumanVerification", Text = "İptal / Kapat", AutoSize = true, Dock = DockStyle.Right };
        var explanation = new Label { Text = "Yalnızca sitenin güvenlik doğrulamasını tamamlayın. İşlem bitince bu pencere kapanır.", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        footer.Controls.Add(explanation);
        footer.Controls.Add(cancel);
        cancel.Click += (_, _) => CancelVerification();
        CancelButton = cancel;
        browser.Dock = DockStyle.Fill;
        Controls.Add(browser);
        Controls.Add(footer);
        PlaceOutsideDesktop();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public void StartInBackground(Form owner)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Owner is not null && Owner != owner) throw new InvalidOperationException("The browser owner cannot be changed.");
        PlaceOutsideDesktop();
        if (!Visible) Show(owner);
        // Windows may apply initial placement/DPI adjustments during Show. Check the final bounds too.
        PlaceOutsideDesktop();
    }

    public void ShowVerification()
    {
        if (_allowClose || IsDisposed) return;
        _verificationVisible = true;
        var owner = Owner;
        if (owner?.WindowState == FormWindowState.Minimized) owner.WindowState = FormWindowState.Normal;
        var area = owner is null ? Screen.PrimaryScreen!.WorkingArea : Screen.FromControl(owner).WorkingArea;
        var width = Math.Min(1020, area.Width);
        var height = Math.Min(750, area.Height);
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        BringToFront();
        Activate();
    }

    public void ReturnToBackground()
    {
        if (_allowClose || IsDisposed) return;
        var wasActive = ContainsFocus || Focused;
        _verificationVisible = false;
        PlaceOutsideDesktop();
        if (wasActive && Owner is { IsDisposed: false, Visible: true } owner) owner.Activate();
    }

    public void CloseAfterShutdown()
    {
        if (IsDisposed) return;
        _allowClose = true;
        Close();
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            CancelVerification();
        }
        base.OnFormClosing(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_placing && !_verificationVisible && !_allowClose && IsHandleCreated && Bounds.IntersectsWith(SystemInformation.VirtualScreen))
            PlaceOutsideDesktop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        base.Dispose(disposing);
    }

    private void CancelVerification()
    {
        if (_verificationVisible) VerificationCancelled?.Invoke();
        ReturnToBackground();
    }

    private void PlaceOutsideDesktop()
    {
        if (_placing || IsDisposed) return;
        _placing = true;
        try
        {
            var desktop = SystemInformation.VirtualScreen;
            ClientSize = _backgroundClientSize;
            Location = new Point(desktop.Right + 64, desktop.Top);
        }
        finally { _placing = false; }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated || _allowClose) return;
        void Reposition()
        {
            if (IsDisposed || _allowClose) return;
            if (_verificationVisible) ShowVerification();
            else PlaceOutsideDesktop();
        }
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)Reposition); }
            catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
        }
        else Reposition();
    }
}
