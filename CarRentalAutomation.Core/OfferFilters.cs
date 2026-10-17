using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CarRentalAutomation.Core;

/// <summary>Matches the Turkish and English fuel/transmission names returned by rental suppliers.</summary>
public static class OfferFilters
{
    public static IReadOnlyList<CarOffer> Apply(IEnumerable<CarOffer> offers, string? fuel, string? transmission)
    {
        ArgumentNullException.ThrowIfNull(offers);
        return offers.Where(offer => MatchesFuel(offer.FuelType, fuel) && MatchesTransmission(offer.Transmission, transmission))
            .OrderBy(offer => offer.Price).ThenBy(offer => offer.Model, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool MatchesFuel(string? actual, string? selected) => IsAll(selected) || MatchesKinds(actual, selected, FuelKind);

    public static bool MatchesTransmission(string? actual, string? selected) => IsAll(selected) || MatchesKinds(actual, selected, TransmissionKind);

    private static bool MatchesKinds(string? actual, string? selected, Func<string, string?> classify)
    {
        var requested = Words(selected).Select(classify).Where(kind => kind is not null).ToHashSet(StringComparer.Ordinal);
        return requested.Count > 0 && Words(actual).Select(classify).Any(kind => kind is not null && requested.Contains(kind));
    }

    private static string? FuelKind(string word) => word switch
    {
        "benzin" or "benzine" or "gasoline" or "petrol" => "petrol",
        "dizel" or "diesel" => "diesel",
        "hibrit" or "hibrid" or "hybrid" or "phev" => "hybrid",
        "elektrik" or "elektrikli" or "electric" or "ev" or "bev" => "electric",
        _ => null
    };

    private static string? TransmissionKind(string word) => word switch
    {
        "otomatik" or "automatik" or "automatic" or "auto" => "automatic",
        "manuel" or "manual" or "duz" => "manual",
        _ => null
    };

    private static bool IsAll(string? value) => Normalize(value) is "" or "all" or "tumu" or "tum" or "hepsi" or "any";

    private static string[] Words(string? value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string? value)
    {
        var text = new StringBuilder();
        foreach (var c in (value ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(c == 'ı' ? 'i' : char.ToLowerInvariant(c));
        }
        return Regex.Replace(text.ToString(), @"[^a-z0-9]+", " ").Trim();
    }
}
