These dependency-free checks use synthetic values and never open the app, access MySQL, contact Yolcu360, or send SMS messages.

Run production JavaScript controller checks with Node.js:

```powershell
node --test tests/yolcu360-login.test.cjs
```

Run the actual C# phone normalizer and embedded-resource checks with .NET 8 on Windows:

```powershell
dotnet run --project tests/LoginRegressionChecks/LoginRegressionChecks.csproj --artifacts-path "$env:TEMP/CarRentalLoginRegression"
```

The JavaScript fixture checks controller behavior, visibility, event dispatch, country and OTP readback, submit guards, challenge reporting, and authenticated markers. It does not validate the live site's current markup or replace testing with a real SMS code entered by the user.

Run the production local offer-filter checks independently of the Windows application:

```powershell
dotnet run --project tests/OfferFilterRegressionChecks/OfferFilterRegressionChecks.csproj --artifacts-path "$env:TEMP/CarRentalOfferFilterRegression"
```

The filter suite contains 16 checks covering Turkish/English fuel and transmission names, `All`/`Tümü`, combined Benzin/Dizel, unknown attribute exclusion, and numeric price ordering.

Run the production search price/resource/serialization checks and the 16 JavaScript search-controller checks:

```powershell
dotnet run --project tests/SearchRegressionChecks/SearchRegressionChecks.csproj --artifacts-path "$env:TEMP/CarRentalSearchRegression"
node --test tests/SearchRegressionChecks/search-controller.test.cjs
```

The .NET search checks require Node.js as well as .NET 8. They execute the generated JavaScript with hostile location text to verify safe serialization, test localized rental-total prices, verify the embedded controller resource, and check UTC `CarOffer.ScrapedAt` serialization. JavaScript fixtures cover extraction, rental totals, loaded/total counts, loading and empty states, session/origin checks, security challenges, single submission, and calendar endpoint validation. The isolated artifact paths avoid replacing a running application's binaries.

Manual end-to-end verification remains necessary: sign in through the WinForms phone/OTP flow, enter a location and future dates/times, click **Ara**, watch the autocomplete/calendar/time selections and normal search submission in WebView2, and compare displayed offers and completeness counts with the website. Confirm the rounded times in WinForms, change fuel/transmission filters, save a collection, reload it, and check its original prices and scrape timestamps. Complete real security challenges in the browser panel. These offline checks do not establish that the full live flow succeeded.
