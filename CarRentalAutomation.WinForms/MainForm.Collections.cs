using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm
{
    private async void SaveClicked(object? sender, EventArgs e)
    {
        if (_closing || _searchInProgress) return;
        if (_criteria is null || _binding.Count == 0) { MessageBox.Show(this, "Search for offers first."); return; }
        var name = Microsoft.VisualBasic.Interaction.InputBox("Collection name:", "Save collection", _criteria.PickupLocation);
        if (_closing || string.IsNullOrWhiteSpace(name)) return;
        var token = _lifetimeCancellation.Token;
        try
        {
            var collectionCriteria = CriteriaWithSelectedFilters(_criteria);
            var originalCars = ((IEnumerable<CarOffer>)_binding.List).ToArray();
            var persisted = await _collections.SaveAsync(name, collectionCriteria, originalCars, token);
            if (_closing) return;
            ApplySavedIds(originalCars, persisted.Cars);
            await RefreshHistoryAsync();
            if (!_closing) _status.Text = "Collection saved.";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Save failed"); }
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
        SavedCollection collection;
        try { collection = await ReadCollectionDetailsAsync(selected); }
        catch (OperationCanceledException) when (_closing) { return; }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Load failed"); return; }
        if (_closing || _searchInProgress || searchVersion != _searchVersion) return;
        _criteria = collection.Criteria; _offers = collection.Cars.ToList();
        _hasResultSet = true;
        _resultContext = $"'{collection.Name}' koleksiyonu yüklendi. Fiyatlar kaydedilmiş anlık görüntülerdir.";
        _location.RestoreSavedLocation(collection.Criteria.PickupLocation); _pickup.Value = collection.Criteria.PickupAt; _return.Value = collection.Criteria.ReturnAt;
        _fuel.SelectedItem = _fuel.Items.Contains(collection.Criteria.FuelFilter) ? collection.Criteria.FuelFilter : "Tümü";
        _transmission.SelectedItem = _transmission.Items.Contains(collection.Criteria.TransmissionFilter) ? collection.Criteria.TransmissionFilter : "Tümü";
        ApplyFilters();
        _tabs.SelectedIndex = 0;
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
            collection = await ReadCollectionDetailsAsync(collection);
        }
        catch (OperationCanceledException) when (_closing) { return; }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "Export failed"); return; }
        if (_closing) return;
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"{collection.Name}.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _reports.Export(collection, dialog.FileName); MessageBox.Show(this, "PNG report exported."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }

    private async Task<SavedCollection> ReadCollectionDetailsAsync(SavedCollection collection)
    {
        if (_collections is MySqlCollectionRepository repository)
            return await repository.GetByIdAsync(collection.Id, _lifetimeCancellation.Token) ?? collection;
        return collection;
    }
}
