using System.Security.Cryptography;
using System.Text;

namespace CarRentalAutomation.Shared;

internal static class IyzicoAuthorization
{
    internal static string Create(string apiKey, string secretKey, string nonce, string path, string body)
    {
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey),
            Encoding.UTF8.GetBytes(nonce + path + body))).ToLowerInvariant();
        var authorization = $"apiKey:{apiKey}&randomKey:{nonce}&signature:{signature}";
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(authorization));
    }
}
