using System.Text.Json;

namespace CarRentalAutomation.Infrastructure;

internal static class Yolcu360LoginScript
{
    private static readonly string Source = LoadSource();

    public static string Create(string action, string? phone = null, string? otp = null, string? phase = null) =>
        "(" + Source + ")(" + JsonSerializer.Serialize(new { action, phone, otp, phase }) + ")";

    private static string LoadSource()
    {
        using var stream = typeof(Yolcu360LoginScript).Assembly.GetManifestResourceStream("CarRentalAutomation.Infrastructure.Yolcu360LoginPage.js")
            ?? throw new InvalidOperationException("Yolcu360 login script resource was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
