using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Identity.Api.Domain;
using Identity.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace UnitTests;

public sealed class IdentitySecurityTests
{
    [Theory]
    [InlineData(" creator ", "CREATOR")]
    [InlineData("Approver.One", "APPROVER.ONE")]
    public void Normalize_is_trimmed_and_case_insensitive(string value, string expected)
    {
        Assert.Equal(expected, UserAccount.Normalize(value));
    }

    [Fact]
    public void Password_hasher_stores_a_verifiable_hash_not_the_password()
    {
        var user = new UserAccount { Username = "creator" };
        var hasher = new PasswordHasher<UserAccount>();
        const string password = "Strong-Local-Test-Password-42!";

        var hash = hasher.HashPassword(user, password);

        Assert.NotEqual(password, hash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, hash, password));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, hash, "wrong-password"));
    }

    [Fact]
    public void Issued_token_contains_identity_role_issuer_audience_and_expiry()
    {
        var options = new JwtOptions
        {
            Issuer = "DistributedDocApproval.Identity",
            Audience = "DistributedDocApproval.Client",
            SigningKey = "a-test-signing-key-that-is-at-least-32-bytes-long",
            AccessTokenMinutes = 30
        };
        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "approver",
            Email = "approver@example.test",
            Role = UserRole.Approver
        };

        var issued = new TokenIssuer(options).Create(user);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);

        Assert.Equal(options.Issuer, token.Issuer);
        Assert.Contains(options.Audience, token.Audiences);
        Assert.Equal(user.Id.ToString(), token.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(UserRole.Approver.ToString(), token.Claims.Single(claim => claim.Type == "role").Value);
        Assert.NotNull(token.Claims.SingleOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Jti));
        Assert.InRange(issued.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            RoleClaimType = "role",
            NameClaimType = "sub"
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(issued.AccessToken, validationParameters, out _);
        Assert.Equal(user.Id.ToString(), principal.FindFirst("sub")?.Value);
        Assert.Equal(UserRole.Approver.ToString(), principal.FindFirst("role")?.Value);
        Assert.True(principal.IsInRole(UserRole.Approver.ToString()));
    }
}
