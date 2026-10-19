namespace CarRentalAutomation.Infrastructure;

internal static class Yolcu360LoginScript
{
    private static readonly string Source = EmbeddedPageScript.Load(
        "CarRentalAutomation.Infrastructure.Yolcu360LoginPage.js",
        "Yolcu360 login script resource was not found.");

    public static string Create(string action, string? phone = null, string? otp = null, string? phase = null) =>
        EmbeddedPageScript.Invoke(Source, new { action, phone, otp, phase });
}
