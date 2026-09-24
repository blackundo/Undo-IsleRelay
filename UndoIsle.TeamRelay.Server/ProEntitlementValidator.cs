using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace UndoIsle.TeamRelay.Server;

public interface IProEntitlementValidator
{
    bool HasCurrentProAccess(string? proof);
}

public sealed class ProEntitlementValidator : IProEntitlementValidator, IDisposable
{
    private const string Issuer = "https://isle-system.modundo.com";
    private const string Audience = "IsleLiveMap.Pro.Agent";
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtyR8IlRGSpQrnYZ3zx8o
        cv/qBG6UAvff9rqbuLK7Z1Onx3frSE11M8Xg796LCh30hKGpJgmnq1bPsD6yUPRm
        HXS0jF2pxlESdHsguvG7w/SxU4FSAZ8T9ata3xAKxsFkP5pm/1M/M4zMUhRtIdcE
        FIwu8tFkRFWvkYYFhP98+S4NIsxswGDjmebaEdbU0GvRDhNAmtu0NJJvSzER7ymW
        1iiUme4lMeeqP4RI56HnBp52OmFl0neg/5+NkJc0yUIBzKlzpI0YKii7YRI5NOnx
        KJOg5EhPyQruedpKNGC3bY5W16XlxVRT8M4Em3QS7fXSXwt9y5A4iohCxOQqntEs
        14vHHiq/IVVWRiAgVHhSGx3wkcW9JPGB2BzXzApWu3Jv1OGq/bn52AUdlts3B/j7
        KqGlNR5MZIw6mZjeStuBbK4+uwkFtrnHEbPYjs7ReN3piW5qERFWMQ4czzWu+mSE
        KPm2B3ed6RwV2uEpRcaxrF3MVNCvnjYfx/qoNIMxjoC1AgMBAAE=
        -----END PUBLIC KEY-----
        """;

    private readonly RSA _rsa = RSA.Create();
    private readonly object _validationGate = new();

    public ProEntitlementValidator() => _rsa.ImportFromPem(PublicKeyPem);

    public bool HasCurrentProAccess(string? proof)
    {
        if (string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        try
        {
            lock (_validationGate)
            {
                var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }
                    .ValidateToken(proof, new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new RsaSecurityKey(_rsa)
                        {
                            CryptoProviderFactory = new CryptoProviderFactory
                            {
                                CacheSignatureProviders = false
                            }
                        },
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(2)
                    }, out var validatedToken);

                return validatedToken is JwtSecurityToken jwt
                       && string.Equals(jwt.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal)
                       && string.Equals(principal.FindFirst("tier")?.Value, "pro", StringComparison.Ordinal)
                       && string.Equals(principal.FindFirst("entitlement_status")?.Value, "active", StringComparison.Ordinal)
                       && string.Equals(principal.FindFirst("token_use")?.Value, "pro_lease", StringComparison.Ordinal)
                       && !string.IsNullOrWhiteSpace(principal.FindFirst("jti")?.Value);
            }
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => _rsa.Dispose();
}
