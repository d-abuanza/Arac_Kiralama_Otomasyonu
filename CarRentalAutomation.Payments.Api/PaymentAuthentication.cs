using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CarRentalAutomation.Payments.Api;

internal sealed class PaymentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ServerConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Length is < 39 or > 520)
            return Task.FromResult(AuthenticateResult.NoResult());
        var hash = Convert.FromHexString(ServerConfiguration.Hash(value[7..]));
        foreach (var client in configuration.Clients)
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(client.TokenHash))) continue;
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, client.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture))], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        return Task.FromResult(AuthenticateResult.Fail("Invalid access token."));
    }
}
