using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

public sealed partial class Yolcu360Browser
{
    private int _searchResponseStatus;

    public async Task<(BrowserOperationResult Result, IReadOnlyList<CarOffer> Offers)> SearchAsync(
        SearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        if (_shutdownRequested) return (new BrowserOperationResult(State, "Uygulama kapatılıyor."), []);
        // A second caller must not queue a duplicate search behind the current operation.
        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return (new BrowserOperationResult(State, "Bir işlem devam ediyor. Tamamlanmasını bekleyin."), []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        var operation = Guid.NewGuid().ToString("N");
        try
        {
            criteria.Validate();
            if (criteria.PickupAt.Minute % 30 != 0 || criteria.ReturnAt.Minute % 30 != 0)
                throw new ArgumentException("Yolcu360 için saatleri 00 veya 30 dakika olarak seçin.");
            await InitializeAsync(token);
            var session = await CheckSessionCoreAsync(token);
            if (!session.Succeeded) return (session, []);
            SetState(Yolcu360SessionState.Searching, "Yolcu360 arama formu hazırlanıyor...");
            // Start from a fresh form in the same browser profile. Previous cards cannot satisfy this search.
            await NavigateAsync("https://yolcu360.com/", token);
            await SearchStepAsync("session", criteria, operation, ["authenticated"], token, TimeSpan.FromSeconds(12));
            await SearchStepAsync("form-ready", criteria, operation, ["ready"], token);
            SetState(Yolcu360SessionState.Searching, "Alış konumu sitedeki listeden seçiliyor...");
            await SearchStepAsync("location-open", criteria, operation, ["location-open"], token);
            await SearchStepAsync("location-type", criteria, operation, ["location-typed"], token);
            await SearchStepAsync("location-select", criteria, operation, ["location-clicked","location-selected"], token);
            await SearchStepAsync("location-check", criteria, operation, ["location-selected"], token);
            SetState(Yolcu360SessionState.Searching, "Alış ve dönüş tarihleri takvimde seçiliyor...");
            await SearchStepAsync("calendar-open", criteria, operation, ["calendar-open"], token);
            await SearchStepAsync("calendar-pickup-day", criteria, operation, ["pickup-date-selected"], token);
            await SearchStepAsync("calendar-return-day", criteria, operation, ["return-date-selected"], token);
            await SearchStepAsync("calendar-check", criteria, operation, ["dates-verified"], token);
            await SearchStepAsync("calendar-confirm", criteria, operation, ["dates-confirmed"], token);
            await SearchStepAsync("pickup-time", criteria, operation, ["time-clicked","time-selected"], token);
            await SearchStepAsync("return-time", criteria, operation, ["time-clicked","time-selected"], token);
            await SearchStepAsync("form-check", criteria, operation, ["ready"], token);

            SetState(Yolcu360SessionState.Searching, "Konum ve tarih/saat doğrulandı. Arama gönderiliyor...");
            _searchResponseStatus = 0;
            var submitted = await SearchDomAsync("submit", criteria, operation, token);
            ThrowSearchPageError(submitted);
            if (submitted.GetProperty("state").GetString() != "submitted")
                throw new InvalidOperationException("Sitenin arama düğmesi etkin değil. Arama gönderilmedi.");
            SetState(Yolcu360SessionState.ExtractingResults, "Canlı araç teklifleri bekleniyor...");
            var results = await WaitForSearchResultsAsync(criteria, operation, token);
            if (results.GetProperty("state").GetString() == "empty")
                return (SetState(Yolcu360SessionState.Authenticated, "Yolcu360 bu konum ve tarihler için araç bulamadı.", true), []);

            var total = results.TryGetProperty("total", out var totalElement) && totalElement.ValueKind == JsonValueKind.Number
                ? totalElement.GetInt32() : (int?)null;
            await ExpandSearchResultsAsync(criteria, operation, total, token);
            var extraction = await SearchDomAsync("extract", criteria, operation, token);
            ThrowSearchPageError(extraction);
            var scrapedAt = DateTime.UtcNow;
            var offers = new List<CarOffer>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            foreach (var row in extraction.GetProperty("rows").EnumerateArray())
            {
                var priceText = row.GetProperty("price").GetString() ?? "";
                var priceMatch = Regex.Match(priceText, @"(?:(?<amount>[\d.,]+)\s*(?<currency>₺|TRY|TL|EUR|€|USD|\$|GBP|£)|(?<currency>₺|TRY|TL|EUR|€|USD|\$|GBP|£)\s*(?<amount>[\d.,]+))", RegexOptions.IgnoreCase);
                if (!priceMatch.Success || !TryParseLocalizedPrice(priceMatch.Groups["amount"].Value, out var price) || price <= 0)
                { skipped++; continue; }
                string Value(string name, string fallback) => row.GetProperty(name).GetString() is { Length: > 0 } value ? value : fallback;
                var source = row.GetProperty("url").GetString();
                if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || uri.Host is not ("yolcu360.com" or "www.yolcu360.com")) source = null;
                var model = Value("model", "Model bilgisi okunamadı");
                var company = Value("company", "Şirket bilgisi okunamadı");
                var key = source ?? $"{model}|{company}|{priceText}|{Value("transmission", "")}|{Value("fuel", "")}";
                if (!seen.Add(key)) continue;
                offers.Add(new CarOffer(model, company, Value("transmission", "Bilinmiyor"), Value("fuel", "Bilinmiyor"),
                    price, NormalizeCurrency(priceMatch.Groups["currency"].Value), source, false) { ScrapedAt = scrapedAt });
            }
            if (offers.Count == 0) throw new InvalidOperationException("Sonuç kartları var ancak kira fiyatları okunamadı. Sayfa yapısı değişmiş olabilir.");
            var completeness = total is { } expected
                ? offers.Count < expected ? $" Sitedeki {expected} teklifin {offers.Count} adedi alındı; liste kısmen yüklendi." : $" Sitedeki {expected} teklif alındı."
                : " Sitenin toplam teklif sayısı doğrulanamadı; yüklenen kartlar alındı.";
            if (skipped > 0) completeness += $" Fiyatı okunamayan {skipped} kart atlandı.";
            return (SetState(Yolcu360SessionState.Authenticated, $"{offers.Count} canlı teklif. Fiyatlar seçilen sürenin toplam kira bedelidir.{completeness}", true), offers);
        }
        catch (SearchLoginRequiredException)
        { return (SetState(Yolcu360SessionState.AuthenticationFailed, "Yolcu360 oturumu doğrulanamadı veya sona erdi. Giriş işlemini başlat'a tıklayın."), []); }
        catch (HumanVerificationCancelledException)
        { return (SetState(Yolcu360SessionState.SearchFailed, "İnsan doğrulaması iptal edildi. Arama durduruldu; otomatik yeniden gönderilmedi."), []); }
        catch (OperationCanceledException)
        { return (SetState(Yolcu360SessionState.SearchFailed, cancellationToken.IsCancellationRequested ? "Arama iptal edildi." : "Arama zaman aşımına uğradı. İnternet bağlantısını kontrol edip tekrar deneyin."), []); }
        catch (Exception ex)
        { return (SetState(Yolcu360SessionState.SearchFailed, "Arama tamamlanamadı: " + ex.Message), []); }
        finally { _operationLock.Release(); }
    }

    private async Task<JsonElement> SearchDomAsync(string action, SearchCriteria criteria, string operation, CancellationToken token)
    {
        if (_navigationError is { } error) throw new IOException("Yolcu360 sayfası yüklenemedi (" + error + ").");
        while (true)
        {
            var result = await EvaluateJsonAsync(Yolcu360SearchScript.Create(action, criteria, operation), token);
            if (result.GetProperty("state").GetString() != "challenge") return result;
            await ResolveHumanVerificationAsync(token);
            // The script's challenge guard precedes every mutation. Resume this step without restarting the search.
        }
    }

    private async Task<JsonElement> SearchStepAsync(string action, SearchCriteria criteria, string operation,
        string[] expected, CancellationToken token, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        var humanWaitAtStart = _humanVerificationTime;
        var state = "unknown";
        while (timer.Elapsed - (_humanVerificationTime - humanWaitAtStart) < (timeout ?? TimeSpan.FromSeconds(15)))
        {
            var result = await SearchDomAsync(action, criteria, operation, token);
            ThrowSearchPageError(result);
            state = result.GetProperty("state").GetString() ?? "unknown";
            if (expected.Contains(state)) { await Task.Delay(150, token); return result; }
            if (state is "location-ambiguous" or "date-outside-calendar" or "date-unavailable")
                throw new InvalidOperationException(state switch
                {
                    "location-ambiguous" => "Sitede seçilen ad ve şehir için birden fazla konum eşleşti. Yanlış konum seçilmemesi için arama gönderilmedi.",
                    "date-outside-calendar" => "Seçilen tarih sitenin takvim aralığının dışında.",
                    _ => "Seçilen tarih sitede kullanılamıyor."
                });
            await Task.Delay(200, token);
        }
        if (action == "session") throw new SearchLoginRequiredException();
        if (action == "location-select") throw new InvalidOperationException($"Seçilen alış konumu sitedeki aynı ad ve şehirle eşleşmedi: {criteria.PickupLocation}. Arama gönderilmedi.");
        throw new InvalidOperationException($"Sitenin arama alanı hazır olmadı ({action}: {state}). Arama gönderilmedi.");
    }

    private async Task<JsonElement> WaitForSearchResultsAsync(SearchCriteria criteria, string operation, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var humanWaitAtStart = _humanVerificationTime;
        string? previous = null;
        var stableSince = DateTime.UtcNow;
        while (timer.Elapsed - (_humanVerificationTime - humanWaitAtStart) < TimeSpan.FromSeconds(60))
        {
            var waitedBeforeRead = _humanVerificationTime;
            var result = await SearchDomAsync("results-state", criteria, operation, token);
            if (_humanVerificationTime != waitedBeforeRead) { previous = null; stableSince = DateTime.UtcNow; }
            ThrowSearchPageError(result);
            if (_searchResponseStatus == 401) throw new SearchLoginRequiredException();
            if (_searchResponseStatus >= 400) throw new IOException($"Yolcu360 arama isteğini kabul etmedi (HTTP {_searchResponseStatus}).");
            var state = result.GetProperty("state").GetString();
            if ((state is "results" or "empty") && _searchResponseStatus is >= 200 and < 300)
            {
                var signature = result.TryGetProperty("signature", out var sig) ? sig.GetString() : "empty";
                if (signature != previous) { previous = signature; stableSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(1)) return result;
            }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("Arama gönderildi ancak tamamlanmış bir sonuç listesi alınamadı. Arama otomatik tekrarlanmadı.");
    }

    private async Task<int> ExpandSearchResultsAsync(SearchCriteria criteria, string operation, int? total, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var humanWaitAtStart = _humanVerificationTime;
        var count = 0;
        var lastGrowth = DateTime.UtcNow;
        var lastObservedHumanWait = _humanVerificationTime;
        while (timer.Elapsed - (_humanVerificationTime - humanWaitAtStart) < TimeSpan.FromSeconds(75))
        {
            var page = await SearchDomAsync("results-state", criteria, operation, token);
            if (_humanVerificationTime != lastObservedHumanWait) lastGrowth = DateTime.UtcNow;
            lastObservedHumanWait = _humanVerificationTime;
            ThrowSearchPageError(page);
            var now = page.TryGetProperty("count", out var itemCount) ? itemCount.GetInt32() : 0;
            if (now > count) { count = now; lastGrowth = DateTime.UtcNow; }
            if (total is { } expected && count >= expected) return count;
            if (DateTime.UtcNow - lastGrowth > TimeSpan.FromSeconds(4)) return count;
            SetState(Yolcu360SessionState.ExtractingResults, total is { } n ? $"Canlı teklifler yükleniyor: {count} / {n}..." : $"Canlı teklifler yükleniyor: {count}...");
            await SearchDomAsync("scroll-results", criteria, operation, token);
            await Task.Delay(350, token);
        }
        return count;
    }

    private static void ThrowSearchPageError(JsonElement result)
    {
        switch (result.GetProperty("state").GetString())
        {
            case "login-required": throw new SearchLoginRequiredException();
            case "challenge": throw new SearchChallengeException();
            case "wrong-origin": throw new InvalidOperationException("Tarayıcı Yolcu360 alan adında değil.");
            case "site-error": throw new IOException("Yolcu360 bir arama hatası gösterdi. Konumu ve tarihleri kontrol edin.");
        }
    }

    private sealed class SearchLoginRequiredException : Exception { }
    private sealed class SearchChallengeException : Exception { }
}
