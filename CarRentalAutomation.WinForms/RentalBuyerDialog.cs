using CarRentalAutomation.Core;
using System.Net.Mail;

namespace CarRentalAutomation.WinForms;

internal sealed class RentalBuyerDialog : Form
{
    private readonly TextBox[] _fields = Enumerable.Range(0, 9).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.DarkRed, MaximumSize = new Size(550, 0) };
    internal RentalBuyer? Buyer { get; private set; }

    internal RentalBuyerDialog(string model, decimal amount, string currency, string? phone = null)
    {
        Text = "Sandbox kiralama — alıcı ve fatura bilgileri";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 580);
        MinimumSize = new Size(620, 600);
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 2, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = new Label { AutoSize = true, MaximumSize = new Size(600, 0), Text = $"{model} — toplam {amount:N2} {currency}\nYalnızca iyzico Sandbox. Yolcu360 üzerinde gerçek rezervasyon oluşturulmaz." };
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        var labels = new[] { "Ad", "Soyad", "T.C. / kimlik no", "E-posta", "Telefon (+ülke kodu)", "Fatura adresi", "Şehir", "Ülke", "Posta kodu (isteğe bağlı)" };
        var limits = new[] { 80, 80, 30, 254, 20, 500, 100, 100, 20 };
        for (var i = 0; i < _fields.Length; i++)
        {
            _fields[i].Name = "buyer" + i;
            _fields[i].AccessibleName = labels[i];
            _fields[i].MaxLength = limits[i];
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.Controls.Add(new Label { Text = labels[i], AutoSize = true }, 0, i + 1);
            layout.Controls.Add(_fields[i], 1, i + 1);
        }
        _fields[4].Text = NormalizePhone(phone);
        _fields[7].Text = "Turkey";
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.Controls.Add(_error, 0, 10); layout.SetColumnSpan(_error, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var sample = new Button { Text = "Sandbox test bilgilerini doldur", AutoSize = true, Name = "btnSandboxBuyer" };
        sample.Click += (_, _) => FillSyntheticBuyer();
        var submit = new Button { Text = "Sandbox ödemesine devam et", AutoSize = true, Name = "btnContinueSandbox" };
        submit.Click += (_, _) => Submit();
        var cancel = new Button { Text = "Vazgeç", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([sample, submit, cancel]);
        layout.Controls.Add(buttons, 0, 11); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = submit; CancelButton = cancel;
        FormClosed += (_, _) => { foreach (var field in _fields) field.Clear(); };
    }

    internal void FillSyntheticBuyer()
    {
        // Explicitly selected Sandbox fixture, never a substitute for database site-login credentials.
        var values = new[] { "Sandbox", "Test", "11111111111", "sandbox@example.test", "+905555555555", "Sandbox test adresi No:1", "Istanbul", "Turkey", "34000" };
        for (var i = 0; i < values.Length; i++) _fields[i].Text = values[i];
    }
    private void Submit()
    {
        var values = _fields.Select(field => field.Text.Trim()).ToArray();
        if (values.Take(8).Any(string.IsNullOrWhiteSpace) || !MailAddress.TryCreate(values[3], out var email) || email.Address != values[3] ||
            values[4].Length is < 11 or > 16 || values[4][0] != '+' || !values[4][1..].All(char.IsAsciiDigit) || values.Any(v => v.Any(char.IsControl)))
        { _error.Text = "Alanları eksiksiz doldurun. Telefon +ülke koduyla başlamalı; e-posta geçerli olmalıdır."; return; }
        Buyer = new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], string.IsNullOrEmpty(values[8]) ? null : values[8]);
        DialogResult = DialogResult.OK;
        Close();
    }
    private static string NormalizePhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("0090", StringComparison.Ordinal)) digits = digits[4..];
        else if (digits.StartsWith("90", StringComparison.Ordinal) && digits.Length == 12) digits = digits[2..];
        if (digits.Length == 11 && digits[0] == '0') digits = digits[1..];
        return digits.Length == 10 && digits[0] == '5' ? "+90" + digits : phone ?? "";
    }
}
