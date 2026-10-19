using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

internal static class Yolcu360SearchScript
{
    private static readonly string Source = EmbeddedPageScript.Load(
        "CarRentalAutomation.Infrastructure.Yolcu360SearchPage.js",
        "Yolcu360 arama betiği bulunamadı.");

    public static string Create(string action, SearchCriteria criteria, string operationId)
    {
        var pickup = PickupLocations.Find(criteria.PickupLocation);
        var request = new
        {
            action,
            operationId,
            location = criteria.PickupLocation,
            locationQuery = pickup?.SearchText ?? criteria.PickupLocation,
            locationNames = pickup?.WebsiteNames ?? [criteria.PickupLocation],
            locationCity = pickup?.City,
            pickupDate = criteria.PickupAt.ToString("yyyy-MM-dd"),
            returnDate = criteria.ReturnAt.ToString("yyyy-MM-dd"),
            pickupTime = criteria.PickupAt.ToString("HH:mm"),
            returnTime = criteria.ReturnAt.ToString("HH:mm")
        };
        return EmbeddedPageScript.Invoke(Source, request);
    }
}
