using CarRentalAutomation.Core;

CarOffer Offer(string model, string transmission, string fuel, decimal price) => new(model, "Synthetic", transmission, fuel, price, "TRY", null);
var offers = new[]
{
    Offer("petrol", "Automatic", "Petrol", 300),
    Offer("diesel", "Manuel", "Diesel", 200),
    Offer("mixed", "Otomatik", "Benzin/Dizel", 400),
    Offer("hybrid", "AUTOMATIC", "HİBRİT", 500),
    Offer("electric", "Düz vites", "Electric", 600),
    Offer("unknown", "Bilinmiyor", "Unknown", 100),
    Offer("unrecognized", "notautomatic", "dieselish", 700)
};
var checks = 0;
void Expect(string? fuel, string? transmission, params string[] expected)
{
    var actual = OfferFilters.Apply(offers, fuel, transmission).Select(offer => offer.Model);
    if (!actual.SequenceEqual(expected)) throw new InvalidOperationException($"Unexpected filter result: fuel={fuel}, transmission={transmission}.");
    checks++;
}
Expect("All", "Tümü", "unknown", "diesel", "petrol", "mixed", "hybrid", "electric", "unrecognized");
Expect(" TÜMÜ ", "all", "unknown", "diesel", "petrol", "mixed", "hybrid", "electric", "unrecognized");
Expect("Benzin", "All", "petrol", "mixed");
Expect("Dizel", "All", "diesel", "mixed");
Expect("Benzin/Dizel", "All", "diesel", "petrol", "mixed");
Expect("Petrol / Diesel", "All", "diesel", "petrol", "mixed");
Expect("Hibrit", "All", "hybrid");
Expect("Elektrik", "All", "electric");
Expect("All", "Manual", "diesel", "electric");
Expect("All", "Otomatik", "petrol", "mixed", "hybrid");
Expect("Dizel", "Automatic", "mixed");
Expect("Unknown", "All");
Expect("All", "Unknown");
Expect("LPG", "All");
if (OfferFilters.MatchesFuel(null, "Benzin") || OfferFilters.MatchesTransmission(null, "Automatic"))
    throw new InvalidOperationException("Missing supplier attributes matched a specific filter.");
checks++;
if (!OfferFilters.MatchesFuel(null, "All") || !OfferFilters.MatchesTransmission(null, "Tümü"))
    throw new InvalidOperationException("All filter excluded an unknown supplier attribute.");
checks++;
Console.WriteLine($"Passed {checks} production offer-filter regression checks. No app, database or network was used.");
