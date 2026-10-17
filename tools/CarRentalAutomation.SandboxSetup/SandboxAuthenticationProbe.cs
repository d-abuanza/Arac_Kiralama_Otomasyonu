using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CarRentalAutomation.SandboxSetup;

internal sealed record ProbeResult(bool Succeeded, string Message, string Reference, string? ProviderCode = null);

// This tool can make only a BIN metadata lookup. It has no checkout, booking or database operations.
internal sealed class SandboxAuthenticationProbe(HttpClient client)
{
    internal const string Path = "/payment/bin/check";
    internal const string TestBin = "55260800";

    internal static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    internal static string CreateAuthorization(SandboxCredentials credentials, string nonce, string body)
    {
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.SecretKey),
            Encoding.UTF8.GetBytes(nonce + Path + body))).ToLowerInvariant();
        var authorization = $"apiKey:{credentials.ApiKey}&randomKey:{nonce}&signature:{signature}";
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(authorization));
    }

    internal async Task<ProbeResult> CheckAsync(SandboxCredentials credentials, CancellationToken cancellationToken = default)
    {
        var reference = "sandbox-check-" + Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new { locale = "tr", conversationId = reference, binNumber = TestBin });
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, SandboxConfiguration.BaseUrl + Path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", CreateAuthorization(credentials, nonce, body));
        request.Headers.Add("x-iyzi-rnd", nonce);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Bound the response and never log its body or provider-supplied free text.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + read > 64 * 1024)
                    return Failed("Sandbox returned an oversized response; authentication was not verified.", reference);
                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failed("Sandbox returned an unexpected response; authentication was not verified.", reference);
            var status = ReadString(root, "status");
            if (response.IsSuccessStatusCode && status == "success" &&
                ReadString(root, "conversationId") == reference && ReadString(root, "binNumber") == TestBin)
                return new(true, "Sandbox authentication verified by BIN lookup. No booking or payment was created.", reference);

            var code = ReadString(root, "errorCode");
            if (code is not { Length: > 0 and <= 10 } || !code.All(char.IsAsciiDigit)) code = null;
            if (code == "1000")
                return new(false, "iyzico Sandbox rejected request authentication (code 1000: Invalid signature). Configuration loaded, but authentication is not verified. Re-copy the API/Secret pair from the same Sandbox merchant account. If it persists, give iyzico support the reference below; do not send the keys.", reference, code);
            if (code is not null)
                return new(false, $"iyzico Sandbox rejected the BIN check (code {code}). Authentication is not verified. Check your Sandbox account configuration and use the reference below for support.", reference, code);
            return Failed($"Sandbox returned an unexpected response (HTTP {(int)response.StatusCode}); authentication was not verified.", reference);
        }
        catch (OperationCanceledException)
        {
            return Failed(cancellationToken.IsCancellationRequested ? "Check cancelled. Authentication was not verified." :
                "Sandbox request timed out after 20 seconds. Check the connection and retry; authentication was not verified.", reference);
        }
        catch (HttpRequestException)
        {
            return Failed("Sandbox could not be reached securely. Check your internet connection, proxy and TLS settings, then retry. Authentication was not verified.", reference);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Failed("Sandbox returned an unreadable response. Authentication was not verified; retry or contact support with the reference below.", reference);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ProbeResult Failed(string message, string reference) => new(false, message + " No booking or payment was created.", reference);
}
