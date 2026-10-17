using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace CarRentalAutomation.Payments.Api;

internal static class LocalDevelopmentSetup
{
    internal static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarRentalAutomation", "Payments");
    internal static string ClientFile => Path.Combine(LocalDirectory, "local-client.dpapi");

    internal static async Task<int> ConfigureAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Local setup requires Windows DPAPI. Configure hosted secrets and key protection separately for another OS."); return 2; }
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CarRentalAutomation.slnx"))) directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException();
            var desktopConfig = Path.Combine(directory.FullName, "CarRentalAutomation.WinForms", "appsettings.json");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(desktopConfig, ct));
            var connectionString = json.RootElement.GetProperty("ConnectionStrings").GetProperty("CarRental").GetString();
            var userId = json.RootElement.TryGetProperty("Application", out var app) &&
                app.TryGetProperty("DemoUserId", out var id) && id.TryGetInt64(out var configured) ? configured : 1L;
            await using (var connection = new MySqlConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = new MySqlCommand("SELECT COUNT(*) FROM users WHERE Id=@id", connection);
                command.Parameters.AddWithValue("@id", userId);
                if (userId <= 0 || Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
                    throw new InvalidOperationException();
            }
            Directory.CreateDirectory(LocalDirectory);
            string clientToken;
            if (File.Exists(ClientFile))
                clientToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(ClientFile, ct), null, DataProtectionScope.CurrentUser));
            else
            {
                clientToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                await File.WriteAllBytesAsync(ClientFile,
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(clientToken), null, DataProtectionScope.CurrentUser), ct);
            }
            if (clientToken.Length != 64 || !clientToken.All(Uri.IsHexDigit)) throw new InvalidOperationException();
            var values = new Dictionary<string, string>
            {
                ["ConnectionStrings:CarRental"] = connectionString!,
                ["Payments:Clients:0:UserId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Payments:Clients:0:TokenHash"] = ServerConfiguration.Hash(clientToken)
            };
            var callback = Environment.GetEnvironmentVariable("PAYMENTS_CALLBACK_URL");
            if (!ServerConfiguration.ValidCallback(callback))
            {
                var config = new ConfigurationBuilder().AddUserSecrets(ServerConfiguration.SecretsId, reloadOnChange: false).Build();
                callback = config["Payments:CallbackUrl"];
                (config as IDisposable)?.Dispose();
            }
            if (!ServerConfiguration.ValidCallback(callback)) callback = await DetectCallbackAsync(ct);
            if (ServerConfiguration.ValidCallback(callback)) values["Payments:CallbackUrl"] = callback!;
            if (!await SaveAsync(values, ct)) throw new IOException();
            Console.WriteLine("Local backend configuration saved outside the repository. Existing Sandbox keys were retained.");
            Console.WriteLine("A reusable client access token is protected with Windows DPAPI; only its hash is configured on the server.");
            Console.WriteLine("User binding verified against MySQL. No schema, booking or payment records were changed.");
            Console.WriteLine(ServerConfiguration.ValidCallback(callback) ? "Public HTTPS callback configured from existing settings/ngrok." :
                "Public HTTPS callback missing. Start ngrok http 5158 and run this setup again.");
            return 0;
        }
        catch
        {
            Console.WriteLine("Local setup failed. Check the desktop database configuration, existing user, profile permissions and .NET SDK. No secret or exception details were printed.");
            return 2;
        }
    }

    private static async Task<string?> DetectCallbackAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var document = JsonDocument.Parse(await client.GetStringAsync("http://127.0.0.1:4040/api/tunnels", ct));
            foreach (var tunnel in document.RootElement.GetProperty("tunnels").EnumerateArray())
            {
                var target = tunnel.GetProperty("config").GetProperty("addr").GetString();
                var publicUrl = tunnel.GetProperty("public_url").GetString();
                if (Uri.TryCreate(target, UriKind.Absolute, out var local) && local.IsLoopback && local.Port == 5158 &&
                    ServerConfiguration.ValidCallback(publicUrl?.TrimEnd('/') + ServerConfiguration.CallbackPath))
                    return publicUrl!.TrimEnd('/') + ServerConfiguration.CallbackPath;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        return null;
    }

    private static async Task<bool> SaveAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var item in new[] { "user-secrets", "set", "--id", ServerConfiguration.SecretsId }) start.ArgumentList.Add(item);
        using var process = Process.Start(start) ?? throw new IOException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(values).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
