using CarRentalAutomation.Core;

namespace CarRentalAutomation.WinForms;

internal sealed class PickupLocationSelector : ComboBox
{
    internal string? SelectedLocation => SelectedItem as string;

    internal PickupLocationSelector()
    {
        Name = "cmbPickupLocation";
        AccessibleName = "Alış yeri";
        DropDownStyle = ComboBoxStyle.DropDownList;
        Width = 345;
        IntegralHeight = true;
        MaxDropDownItems = 10;
        Items.AddRange(PickupLocations.Options.Select(option => (object)option.DisplayName).ToArray());
        SelectedItem = PickupLocations.DefaultDisplayName;
        UpdateDropDownWidth();
    }

    internal string RequireSelection() => SelectedLocation is { Length: > 0 } selected
        ? selected : throw new ArgumentException("Lütfen Alış yeri listesinden bir konum seçin.");

    internal void RestoreSavedLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) { SelectedIndex = -1; return; }
        if (!Items.Contains(location)) Items.Add(location);
        SelectedItem = location;
        UpdateDropDownWidth();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateDropDownWidth();
    }

    private void UpdateDropDownWidth()
    {
        var longest = Items.Cast<string>().Select(value => TextRenderer.MeasureText(value, Font).Width).DefaultIfEmpty(0).Max();
        DropDownWidth = Math.Max(Width, longest + SystemInformation.VerticalScrollBarWidth + 24);
    }
}
