using CarRentalAutomation.Core;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm
{
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
            var input = new SearchCriteria(_location.RequireSelection(), _pickup.Value, _return.Value);
            input.Validate();
            var criteria = new SearchCriteria(input.PickupLocation, RoundUpToHalfHour(input.PickupAt), RoundUpToHalfHour(input.ReturnAt));
            criteria.Validate();
            var adjustedTimes = criteria.PickupAt != input.PickupAt || criteria.ReturnAt != input.ReturnAt;
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
        value = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
        var step = TimeSpan.TicksPerMinute * 30;
        var remainder = value.Ticks % step;
        return remainder == 0 ? value : value.AddTicks(step - remainder);
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

    private SearchCriteria CriteriaWithSelectedFilters(SearchCriteria criteria) => criteria with
    {
        FuelFilter = _fuel.SelectedItem?.ToString() ?? "Tümü",
        TransmissionFilter = _transmission.SelectedItem?.ToString() ?? "Tümü"
    };

    private void SetBusy(bool busy, string message) { UseWaitCursor = busy; _status.Text = message; }
}
