namespace CarRentalAutomation.Core;

public sealed record PickupLocationOption(string DisplayName, string SearchText, IReadOnlyList<string> WebsiteNames, string City)
{
    public override string ToString() => DisplayName;
}

public static class PickupLocations
{
    public const string DefaultDisplayName = "İstanbul - İstanbul Havalimanı";

    public static IReadOnlyList<PickupLocationOption> Options { get; } = Array.AsReadOnly(new[]
    {
        Location("İstanbul - Sabiha Gökçen Havalimanı", "Sabiha Gökçen Havalimanı", "İstanbul"),
        Location("Ankara - Ankara Esenboğa Havalimanı", "Ankara Esenboğa Havalimanı", "Ankara", "Esenboğa Havalimanı"),
        Location("İzmir - İzmir Adnan Menderes Havalimanı", "İzmir Adnan Menderes Havalimanı", "İzmir", "Adnan Menderes Havalimanı"),
        Location(DefaultDisplayName, "İstanbul Havalimanı", "İstanbul"),
        Location("İstanbul - Kadıköy", "Kadıköy", "İstanbul"),
        Location("İstanbul - Yenibosna", "Yenibosna", "İstanbul"),
        Location("İzmir - Alsancak", "Alsancak", "İzmir")
    });

    public static PickupLocationOption? Find(string? displayName) =>
        Options.FirstOrDefault(option => string.Equals(option.DisplayName, displayName, StringComparison.Ordinal));

    private static PickupLocationOption Location(string displayName, string searchText, string city, params string[] websiteAliases) =>
        new(displayName, searchText, Array.AsReadOnly(new[] { searchText, displayName }.Concat(websiteAliases).ToArray()), city);
}
