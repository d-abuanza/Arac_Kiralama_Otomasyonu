using System.Security.Cryptography;
using System.Text;

namespace CarRentalAutomation.Payments.Api;

internal sealed class ServerConfiguration
{
    internal const string SecretsId = "CarRentalAutomation.Payments.Development";
    internal const string SandboxUrl = "https://sandbox-api.iyzipay.com";
    internal const string CallbackPath = "/api/payments/iyzico/callback";
    internal string? ConnectionString { get; }
    internal string? ApiKey { get; }
    internal string? SecretKey { get; }
    internal string? CallbackUrl { get; }
    internal IReadOnlyList<ClientIdentity> Clients { get; }
    internal IReadOnlyList<string> Missing { get; }
    internal bool IsConfigured => Missing.Count == 0;

    internal ServerConfiguration(IConfiguration settings)
    {
        ConnectionString = Environment.GetEnvironmentVariable("CARRENTAL_CONNECTION_STRING") ?? settings.GetConnectionString("CarRental");
        var api = Environment.GetEnvironmentVariable("IYZICO_SANDBOX_API_KEY");
        var secret = Environment.GetEnvironmentVariable("IYZICO_SANDBOX_SECRET_KEY");
        var environmentPair = api is not null || secret is not null;
        ApiKey = environmentPair ? api : settings["Iyzico:ApiKey"];
        SecretKey = environmentPair ? secret : settings["Iyzico:SecretKey"];
        CallbackUrl = Environment.GetEnvironmentVariable("PAYMENTS_CALLBACK_URL") ?? settings["Payments:CallbackUrl"];
        var clients = new List<ClientIdentity>();
        foreach (var section in settings.GetSection("Payments:Clients").GetChildren())
            if (long.TryParse(section["UserId"], out var userId) && userId > 0 && IsHash(section["TokenHash"]))
                clients.Add(new(userId, section["TokenHash"]!));
        var validClients = clients.Count > 0 && clients.Select(c => c.TokenHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() == clients.Count;
        Clients = validClients ? clients : [];
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString)) missing.Add("ConnectionStrings:CarRental");
        if (!ValidKey(ApiKey) || !ValidKey(SecretKey) || ApiKey == SecretKey) missing.Add("Iyzico Sandbox key pair");
        var baseUrl = Environment.GetEnvironmentVariable("IYZICO_BASE_URL") ?? settings["Iyzico:BaseUrl"];
        if (baseUrl is not null && baseUrl != SandboxUrl) missing.Add("Iyzico Sandbox base URL");
        if (!ValidCallback(CallbackUrl)) missing.Add("Payments:CallbackUrl (public HTTPS)");
        if (!validClients)
            missing.Add("Payments:Clients (unique user-bound token hashes)");
        Missing = missing;
    }

    internal void RequireReady()
    {
        if (!IsConfigured) throw new PaymentApiException(503, "Ödeme sunucusu yapılandırması eksik. Yerel backend kurulumunu tamamlayın.");
    }

    internal static bool ValidCallback(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && !uri.IsLoopback && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == CallbackPath;
    private static bool ValidKey(string? value) => value is { Length: > 8 and <= 1024 } &&
        value.StartsWith("sandbox-", StringComparison.Ordinal) && value.All(c => c is >= '!' and <= '~' && c is not '\'' and not '"') &&
        !value.Contains("...", StringComparison.Ordinal) && !value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public override string ToString() => "Payment server configuration [redacted]";
}

internal sealed record ClientIdentity(long UserId, string TokenHash);
