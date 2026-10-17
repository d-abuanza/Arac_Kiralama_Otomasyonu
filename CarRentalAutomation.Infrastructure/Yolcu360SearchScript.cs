using System.Text.Json;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

internal static class Yolcu360SearchScript
{
    private static readonly string Source = Load();
    public static string Create(string action, SearchCriteria criteria, string operationId)
    {
        var request = new
        {
            action, operationId, location = criteria.PickupLocation,
            pickupDate = criteria.PickupAt.ToString("yyyy-MM-dd"), returnDate = criteria.ReturnAt.ToString("yyyy-MM-dd"),
            pickupTime = criteria.PickupAt.ToString("HH:mm"), returnTime = criteria.ReturnAt.ToString("HH:mm")
        };
        return "(" + Source + ")(" + JsonSerializer.Serialize(request) + ")";
    }
    private static string Load()
    {
        using var stream = typeof(Yolcu360SearchScript).Assembly.GetManifestResourceStream("CarRentalAutomation.Infrastructure.Yolcu360SearchPage.js")
            ?? throw new InvalidOperationException("Yolcu360 arama betiği bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
