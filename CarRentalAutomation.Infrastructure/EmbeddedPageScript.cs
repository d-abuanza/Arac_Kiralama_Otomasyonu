using System.Text.Json;

namespace CarRentalAutomation.Infrastructure;

internal static class EmbeddedPageScript
{
    public static string Load(string resourceName, string missingResourceMessage)
    {
        using var stream = typeof(EmbeddedPageScript).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(missingResourceMessage);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Invoke<TRequest>(string source, TRequest request) =>
        "(" + source + ")(" + JsonSerializer.Serialize(request) + ")";
}
