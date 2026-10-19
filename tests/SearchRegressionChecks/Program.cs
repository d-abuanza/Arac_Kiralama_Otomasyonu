using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;

var checks = 0;
var assembly = typeof(Yolcu360Browser).Assembly;
var parse = typeof(Yolcu360Browser).GetMethod("TryParseLocalizedPrice", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Production price parser was not found.");
foreach (var (input, expected) in new (string, decimal)[]
{
    ("7.706", 7706m), ("5.000,00", 5000m), ("1.234.567,89", 1234567.89m),
    ("1,234.56", 1234.56m), ("1,234,567.89", 1234567.89m), ("3.000,50", 3000.50m),
    ("1234,5", 1234.5m), ("1234.56", 1234.56m), ("1234", 1234m), (" 7.706 ", 7706m)
})
{
    object?[] values = [input, 0m];
    if (parse.Invoke(null, values) is not true || (decimal)values[1]! != expected)
        throw new InvalidOperationException($"Production price parser misread {input}.");
    checks++;
}
foreach (var input in new[] { "", " ", "-5000", "+5000", "1e3", "NaN", "Infinity", "1..2", "1,,2", "abc", "5.000 TL", "5 000", new string('9', 80) })
{
    object?[] values = [input, 0m];
    if (parse.Invoke(null, values) is not false)
        throw new InvalidOperationException("An invalid or unsupported price was accepted.");
    checks++;
}
using var resource = assembly.GetManifestResourceStream("CarRentalAutomation.Infrastructure.Yolcu360SearchPage.js")
    ?? throw new InvalidOperationException("Production search script was not embedded.");
using var reader = new StreamReader(resource);
var source = reader.ReadToEnd();
if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Embedded search script is empty.");
checks++;
var create = assembly.GetType("CarRentalAutomation.Infrastructure.Yolcu360SearchScript")
    ?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("Production search-script factory was not found.");
var hostileLocation = "Istanbul '\"\\\r\n\u2028\u2029</script>); globalThis.__injected = true; //";
var hostileOperation = "op\"); globalThis.__injected = true; //";
var criteria = new SearchCriteria(hostileLocation, new DateTime(2030, 12, 5, 9, 30, 0), new DateTime(2030, 12, 8, 17, 0, 0));
var script = create.Invoke(null, ["extract", criteria, hostileOperation]) as string
    ?? throw new InvalidOperationException("Production script factory returned no script.");
var prefix = "(" + source + ")(";
if (!script.StartsWith(prefix, StringComparison.Ordinal) || !script.EndsWith(')'))
    throw new InvalidOperationException("Search-script function wrapper is malformed.");
using (var request = JsonDocument.Parse(script[prefix.Length..^1]))
{
    var json = request.RootElement;
    if (json.GetProperty("action").GetString() != "extract"
        || json.GetProperty("location").GetString() != hostileLocation
        || json.GetProperty("locationQuery").GetString() != hostileLocation
        || json.GetProperty("locationNames").GetArrayLength() != 1
        || json.GetProperty("locationNames")[0].GetString() != hostileLocation
        || json.GetProperty("locationCity").ValueKind != JsonValueKind.Null
        || json.GetProperty("operationId").GetString() != hostileOperation
        || json.GetProperty("pickupDate").GetString() != "2030-12-05"
        || json.GetProperty("returnDate").GetString() != "2030-12-08"
        || json.GetProperty("pickupTime").GetString() != "09:30"
        || json.GetProperty("returnTime").GetString() != "17:00")
        throw new InvalidOperationException("Search request serialization changed values or local dates/times.");
    checks++;
}
foreach (var location in PickupLocations.Options)
{
    var mappedCriteria = criteria with { PickupLocation = location.DisplayName };
    var mappedScript = (string?)create.Invoke(null, ["location-type", mappedCriteria, "mapped-search"])
        ?? throw new InvalidOperationException("Mapped pickup script is missing.");
    using var request = JsonDocument.Parse(mappedScript[prefix.Length..^1]);
    var json = request.RootElement;
    if (json.GetProperty("location").GetString() != location.DisplayName
        || json.GetProperty("locationQuery").GetString() != location.SearchText
        || json.GetProperty("locationCity").GetString() != location.City
        || !json.GetProperty("locationNames").EnumerateArray().Select(x => x.GetString()).SequenceEqual(location.WebsiteNames))
        throw new InvalidOperationException("A dropdown pickup changed its persisted label or website mapping.");
    checks++;
}
if (PickupLocations.Find("Ankara - Ankara Esenboğa Havalimanı")?.WebsiteNames.Contains("Esenboğa Havalimanı") is not true
    || PickupLocations.Find("İzmir - İzmir Adnan Menderes Havalimanı")?.WebsiteNames.Contains("Adnan Menderes Havalimanı") is not true
    || PickupLocations.Find("Previously saved arbitrary location") is not null)
    throw new InvalidOperationException("Verified website aliases or saved legacy pickup compatibility changed.");
checks++;
using (var node = Process.Start(new ProcessStartInfo("node")
{
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    UseShellExecute = false, CreateNoWindow = true,
    ArgumentList = { "-e", "const vm=require('node:vm'); const src=require('node:fs').readFileSync(0,'utf8'); const context=vm.createContext({location:{protocol:'https:',hostname:'example.invalid'}}); const result=new vm.Script(src).runInContext(context,{timeout:1000}); if(result.state!=='wrong-origin'||context.__injected!==undefined)process.exit(1);" }
}) ?? throw new InvalidOperationException("Node.js could not start."))
{
    await node.StandardInput.WriteAsync(script);
    node.StandardInput.Close();
    var error = await node.StandardError.ReadToEndAsync();
    await node.WaitForExitAsync();
    if (node.ExitCode != 0) throw new InvalidOperationException("Generated JavaScript did not parse safely: " + error);
    checks++;
}
var scrapedAt = new DateTime(2030, 12, 1, 12, 0, 0, DateTimeKind.Utc);
var offer = new CarOffer("Synthetic", "Fixture", "Manual", "Petrol", 7706m, "TRY", "https://yolcu360.com/arac-kiralama/detail/synthetic") { ScrapedAt = scrapedAt };
var roundtrip = JsonSerializer.Deserialize<CarOffer>(JsonSerializer.Serialize(offer));
if (roundtrip?.ScrapedAt != scrapedAt || roundtrip.ScrapedAt.Value.Kind != DateTimeKind.Utc || roundtrip.IsMockData)
    throw new InvalidOperationException("A live offer lost its UTC scrape timestamp during serialization.");
checks++;
var readOffers = typeof(Yolcu360Browser).GetMethod("ReadOffers", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Production offer mapper was not found.");
using (var extraction = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    rows = new[]
    {
        new { model = "Renault Clio", company = "Supplier", transmission = "Otomatik", fuel = "Benzin", price = "3.000,50 TRY", url = "https://yolcu360.com/arac-kiralama/detail/one" },
        new { model = "Repeated", company = "Supplier", transmission = "Otomatik", fuel = "Benzin", price = "3.000,50 TRY", url = "https://yolcu360.com/arac-kiralama/detail/one" },
        new { model = "Invalid", company = "Supplier", transmission = "Manuel", fuel = "Dizel", price = "unreadable", url = "" },
        new { model = "", company = "", transmission = "", fuel = "", price = "EUR 1,234.56", url = "https://example.invalid/offer" }
    }
})))
{
    object?[] arguments = [extraction.RootElement, 0];
    var mapped = readOffers.Invoke(null, arguments) as IReadOnlyList<CarOffer>
        ?? throw new InvalidOperationException("Production mapper returned no collection.");
    if (mapped.Count != 2 || (int)arguments[1]! != 1)
        throw new InvalidOperationException("Offer deduplication or skipped-price count changed.");
    checks++;
    if (mapped[0].Model != "Renault Clio" || mapped[0].RentalCompany != "Supplier" || mapped[0].Price != 3000.50m || mapped[0].Currency != "TRY")
        throw new InvalidOperationException("Production offer attributes changed during mapping.");
    checks++;
    if (mapped[1].Price != 1234.56m || mapped[1].Currency != "EUR" || mapped[1].SourceUrl is not null || mapped[1].Model != "Model bilgisi okunamadı" || mapped[1].FuelType != "Bilinmiyor")
        throw new InvalidOperationException("Production offer fallback or source URL policy changed.");
    checks++;
    if (mapped.Any(item => item.IsMockData || item.ScrapedAt?.Kind != DateTimeKind.Utc) || mapped[0].ScrapedAt != mapped[1].ScrapedAt)
        throw new InvalidOperationException("Mapped offers lost their shared live UTC timestamp.");
    checks++;
}
Console.WriteLine($"Passed {checks} production search price/resource/serialization checks. No app, browser, database or network was used.");
