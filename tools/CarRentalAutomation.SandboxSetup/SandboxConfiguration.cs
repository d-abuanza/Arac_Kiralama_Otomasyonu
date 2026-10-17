using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace CarRentalAutomation.SandboxSetup;

// Server setup tooling only. The rental desktop application must not reference this project.
internal sealed class SandboxCredentials(string apiKey, string secretKey)
{
    internal string ApiKey { get; } = apiKey;
    internal string SecretKey { get; } = secretKey;
    public override string ToString() => "Sandbox credentials [redacted]";
}

internal sealed record ConfigurationResult(SandboxCredentials? Credentials, string Source, string Message)
{
    internal bool IsValid => Credentials is not null;
}

internal static class SandboxConfiguration
{
    internal const string SecretsId = "CarRentalAutomation.Payments.Development";
    internal const string BaseUrl = "https://sandbox-api.iyzipay.com";

    internal static bool HasEnvironmentCredentials =>
        Environment.GetEnvironmentVariable("IYZICO_SANDBOX_API_KEY") is not null ||
        Environment.GetEnvironmentVariable("IYZICO_SANDBOX_SECRET_KEY") is not null;

    internal static ConfigurationResult Load()
    {
        var environmentApiKey = Environment.GetEnvironmentVariable("IYZICO_SANDBOX_API_KEY");
        var environmentSecretKey = Environment.GetEnvironmentVariable("IYZICO_SANDBOX_SECRET_KEY");
        var environmentBaseUrl = Environment.GetEnvironmentVariable("IYZICO_BASE_URL");
        if (environmentApiKey is not null || environmentSecretKey is not null)
            return Resolve(null, null, environmentApiKey, environmentSecretKey, environmentBaseUrl);
        try
        {
            var settings = new ConfigurationBuilder().AddUserSecrets(SecretsId, reloadOnChange: false).Build();
            try
            {
                return Resolve(settings["Iyzico:ApiKey"], settings["Iyzico:SecretKey"],
                    environmentApiKey, environmentSecretKey, environmentBaseUrl);
            }
            finally { (settings as IDisposable)?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return new(null, ".NET User Secrets", "Local User Secrets could not be read. Check your profile permissions and secrets JSON format. Secret values have not been printed.");
        }
    }

    internal static ConfigurationResult Resolve(string? storedApiKey, string? storedSecretKey,
        string? environmentApiKey, string? environmentSecretKey, string? environmentBaseUrl)
    {
        // Treat the environment pair atomically: never combine one account's key with another source's secret.
        var useEnvironment = environmentApiKey is not null || environmentSecretKey is not null;
        var source = useEnvironment ? "server environment" : ".NET User Secrets";
        if (environmentBaseUrl is not null && environmentBaseUrl != BaseUrl)
            return new(null, source, "Sandbox only: IYZICO_BASE_URL must be exactly https://sandbox-api.iyzipay.com, or unset it to use that default.");

        var apiKey = useEnvironment ? environmentApiKey : storedApiKey;
        var secretKey = useEnvironment ? environmentSecretKey : storedSecretKey;
        var validationError = ValidatePair(apiKey, secretKey);
        if (validationError is not null)
            return new(null, source, validationError + (useEnvironment
                ? " Both environment variables must contain the same account's complete pair; saved User Secrets are not mixed with them."
                : " Run this setup tool with --configure and enter both keys in its local window."));

        return new(new SandboxCredentials(apiKey!, secretKey!), source,
            "Configuration loaded. Both key values are present and have Sandbox format. Provider authentication still needs to be checked.");
    }

    internal static string? ValidatePair(string? apiKey, string? secretKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey))
            return "The Sandbox API Key and Secret Key are both required.";
        foreach (var (name, value) in new[] { ("API Key", apiKey), ("Secret Key", secretKey) })
        {
            if (value.Length > 1024 || value.Any(c => c < '!' || c > '~' || c is '\'' or '"'))
                return $"The {name} contains quotes, whitespace or unsupported characters. Paste only the complete value from the Sandbox dashboard.";
            if (!value.StartsWith("sandbox-", StringComparison.Ordinal) || value.Length <= "sandbox-".Length ||
                value.Contains("...", StringComparison.Ordinal) || value.Contains('…') ||
                value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) || value.Contains('<') || value.Contains('>'))
                return $"The {name} must be the complete Sandbox value beginning with sandbox-, without example placeholders.";
        }
        if (apiKey == secretKey) return "The API Key and Secret Key are identical. Copy each value from its own dashboard field.";
        return null;
    }

    internal static async Task<bool> SaveUserSecretsAsync(SandboxCredentials credentials, CancellationToken cancellationToken,
        string userSecretsId = SecretsId)
    {
        // Use the supported Secret Manager CLI; never place values in command arguments or a repo file.
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "user-secrets", "set", "--id", userSecretsId }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) return false;
            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            // Discard CLI output. An unexpected CLI error must not echo its input into our logs.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Iyzico:ApiKey"] = credentials.ApiKey,
                ["Iyzico:SecretKey"] = credentials.SecretKey
            });
            await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            try { if (started && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return false;
        }
    }
}
