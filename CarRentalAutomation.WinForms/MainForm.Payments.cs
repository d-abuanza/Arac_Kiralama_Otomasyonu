using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;

namespace CarRentalAutomation.WinForms;

public sealed partial class MainForm
{
    private readonly IRentalPaymentApi? _payments;
    private readonly RentalPaymentCoordinator? _rentalPayments;
    private bool _paymentInProgress;
    private PaymentsPage? _paymentsPage;

    private void ApplySavedIds(IReadOnlyList<CarOffer> originals, IReadOnlyList<CarOffer> saved)
    {
        for (var i = 0; i < Math.Min(originals.Count, saved.Count); i++)
        {
            var index = _offers.FindIndex(offer => ReferenceEquals(offer, originals[i]));
            if (index >= 0) _offers[index] = saved[i];
        }
        ApplyFilters();
    }

    private async Task RunRentalPaymentAsync(CarOffer car)
    {
        if (_payments is null || _rentalPayments is null) return;
        if (car.IsMockData) { MessageBox.Show(this, "Demo araçlar ödenemez. Canlı veya kayıtlı gerçek teklif seçin.", "Sandbox"); return; }
        if (_criteria is not null && (!string.Equals(_location.SelectedLocation, _criteria.PickupLocation, StringComparison.Ordinal) ||
            _pickup.Value != _criteria.PickupAt || _return.Value != _criteria.ReturnAt))
        { MessageBox.Show(this, "Alış yeri veya tarihler değişti. Bu bilgilerle önce yeni arama yapın; seçili teklif eski aramaya aittir.", "Teklif bilgileri"); return; }
        _paymentInProgress = true;
        UpdateBrowserControls();
        var ct = _lifetimeCancellation.Token;
        try
        {
            RentalAttempt? attempt = car.Id > 0 ? await _rentalPayments.ReadAsync(car.Id, ct) : null;
            if (attempt?.PaymentId is { } previousPaymentId)
            {
                var previous = await _payments.GetAsync(previousPaymentId, ct);
                await _rentalPayments.RecordAsync(car.Id, previous, ct);
                if (RentalPaymentStates.Succeeded(previous.Status))
                {
                    if (MessageBox.Show(this, $"Bu araç için rezervasyon #{previous.BookingId} zaten ödendi. Ayrı bir kiralama simülasyonu oluşturulsun mu?",
                        "Yeni ve ayrı kiralama", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    await _rentalPayments.StartSeparateRentalAsync(car.Id, ct);
                    attempt = null;
                }
                else
                {
                    _status.Text = "Mevcut rezervasyon ve ödeme denemesinden devam ediliyor…";
                    ShowCheckout(car.Id, await _payments.ResumeAsync(previousPaymentId, ct));
                    return;
                }
            }
            if (attempt is null && MessageBox.Show(this,
                $"{car.Model}\n{_criteria?.PickupLocation}\nAlış: {_criteria?.PickupAt:dd.MM.yyyy HH:mm} — dönüş: {_criteria?.ReturnAt:dd.MM.yyyy HH:mm}\nToplam kira bedeli: {car.Price:N2} {car.Currency}\n\nKiralama simülasyonu oluşturulup iyzico Sandbox ödeme formu açılsın mı? Yolcu360 üzerinde gerçek rezervasyon yapılmaz." +
                (car.Id <= 0 ? "\nSeçilen teklif önce koleksiyona kaydedilecek." : ""),
                "Sandbox kiralama onayı", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            if (car.Id <= 0)
            {
                if (_criteria is null) throw new PaymentClientException("Önce geçerli bir araç araması yapın.");
                var criteria = _criteria with { FuelFilter = _fuel.SelectedItem?.ToString() ?? "Tümü", TransmissionFilter = _transmission.SelectedItem?.ToString() ?? "Tümü" };
                var collection = await _collections.SaveAsync(criteria.PickupLocation, criteria, [car], ct);
                if (collection.Cars.Count != 1 || collection.Cars[0].Id <= 0) throw new PaymentClientException("Araç veritabanı kimliği alınamadı; ödeme başlatılmadı.");
                ApplySavedIds([car], collection.Cars);
                car = collection.Cars[0];
                await RefreshHistoryAsync();
            }
            _status.Text = "Bekleyen rezervasyon sunucuda oluşturuluyor veya mevcut istek kurtarılıyor…";
            var booking = await _rentalPayments.EnsureBookingAsync(car.Id, ct);
            if (_closing) return;
            string? phone = null;
            try { phone = await _siteCredentials.GetPhoneNumberAsync(_userId, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* The buyer can enter a phone; site login is not changed. */ }
            using var buyerDialog = new RentalBuyerDialog(booking.Model, booking.Amount, booking.Currency, phone);
            if (buyerDialog.ShowDialog(this) != DialogResult.OK || buyerDialog.Buyer is null)
            { _status.Text = $"Rezervasyon #{booking.Id} ödeme bekliyor. Ödeme formu başlatılmadı; aynı araçtan devam edebilirsiniz."; return; }
            _status.Text = "iyzico Sandbox ödeme formu hazırlanıyor…";
            var checkout = await _rentalPayments.StartAsync(car.Id, buyerDialog.Buyer, retryFailed: false, ct);
            if (!_closing) ShowCheckout(car.Id, checkout);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (PaymentClientException exception)
        {
            if (!_closing) { _status.Text = exception.Message; MessageBox.Show(this, exception.Message, "Sandbox ödeme durumu", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        catch (InvalidOperationException exception)
        {
            if (!_closing) { _status.Text = exception.Message; MessageBox.Show(this, exception.Message, "Kiralama durumu"); }
        }
        catch (Exception)
        {
            if (!_closing) { _status.Text = "Kiralama işlemi tamamlanamadı. Aynı araçtan devam ederek mevcut denemeyi kontrol edin; yeni ödeme oluşturmayın."; MessageBox.Show(this, _status.Text, "Sandbox"); }
        }
        finally
        {
            _paymentInProgress = false;
            UpdateBrowserControls();
            if (!_closing && _paymentsPage is not null) await _paymentsPage.RefreshAsync();
        }
    }

    private void ShowCheckout(long carId, RentalCheckout checkout)
    {
        using var dialog = new RentalCheckoutForm(_payments!, _rentalPayments!, carId, checkout);
        dialog.ShowDialog(this);
        if (_closing) return;
        var payment = dialog.LatestPayment;
        _status.Text = $"Sandbox ödeme #{payment.Id}: {RentalPaymentStates.Label(payment.Status)}. Rezervasyon #{payment.BookingId}." +
            (RentalPaymentStates.Succeeded(payment.Status) ? " Kiralama simülasyonu onaylandı." : "") +
            (dialog.LastError is null ? "" : " " + dialog.LastError);
    }
}
