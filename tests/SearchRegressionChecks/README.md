These checks run production C# price parsing, request serialization and embedded resources, plus the production JavaScript controller against synthetic DOM fixtures. They do not open WinForms or a browser, access MySQL, contact Yolcu360, or submit searches/SMS.

```powershell
dotnet run --project tests/SearchRegressionChecks/SearchRegressionChecks.csproj --artifacts-path "$env:TEMP/CarRentalSearchRegression"
node --test tests/SearchRegressionChecks/search-controller.test.cjs
```

The .NET checks also require Node.js to compile and execute the exact generated script with hostile location/operation text. They verify that the values remain inert serialized data and local search dates/times survive the roundtrip.

The DOM checks cover actual extraction logic, total versus loaded counts, loading/empty/error guards, authenticated origin markers, security challenges, and prevention of duplicate or premature submission. Calendar fixtures verify partial pickup selection, distinct or same-day endpoints, the conditional return-date label, and rejection of stale or unavailable dates. Fixtures cannot prove that the live site's current DOM matches these selectors; live UI verification is still required.
