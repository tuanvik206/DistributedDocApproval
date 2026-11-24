using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Identity.Api.Contracts;
using Identity.Api.Domain;
using Identity.Api.Infrastructure;
using Identity.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Identity.Api.Endpoints;

public static partial class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous();

        endpoints.MapGet("/api/users/me", GetCurrentUserAsync).RequireAuthorization();
        endpoints.MapGet("/api/users/approvers", GetApproversAsync)
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Creator.ToString()));

        var adminUsers = endpoints.MapGroup("/api/users").RequireAuthorization("AdminOnly");
        adminUsers.MapGet("", GetUsersAsync);
        adminUsers.MapPost("", CreateUserAsync);
        adminUsers.MapPatch("/{id:guid}/status", ChangeStatusAsync);
        adminUsers.MapPatch("/{id:guid}/role", ChangeRoleAsync);

        endpoints.MapPost("/internal/approvers/validate", ValidateApproversAsync)
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IdentityDbContext db,
        IPasswordHasher<UserAccount> passwordHasher,
        TokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        var usernameOrEmail = request.UsernameOrEmail?.Trim();
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrEmpty(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["Username/email and password are required."]
            });
        }

        var normalized = UserAccount.Normalize(usernameOrEmail);
        var user = await db.Users.SingleOrDefaultAsync(
            account => account.NormalizedUsername == normalized || account.NormalizedEmail == normalized,
            cancellationToken);

        if (user is null || !user.IsActive)
        {
            return InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(cancellationToken);
        }

        var issuedToken = tokenIssuer.Create(user);
        return Results.Ok(new LoginResponse(
            issuedToken.AccessToken,
            "Bearer",
            issuedToken.ExpiresAtUtc,
            ToResponse(user)));
    }

    private static async Task<IResult> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(account => account.Id == userId, cancellationToken);
        return user is null ? Results.NotFound() : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> GetApproversAsync(
        IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        var approvers = await db.Users.AsNoTracking()
            .Where(user => user.Role == UserRole.Approver && user.IsActive)
            .OrderBy(user => user.FullName)
            .Select(user => new UserResponse(
                user.Id,
                user.Username,
                user.FullName,
                user.Email,
                user.Role.ToString(),
                user.IsActive,
                user.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(approvers);
    }

    private static async Task<IResult> GetUsersAsync(
        IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new UserResponse(
                user.Id,
                user.Username,
                user.FullName,
                user.Email,
                user.Role.ToString(),
                user.IsActive,
                user.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        IdentityDbContext db,
        IPasswordHasher<UserAccount> passwordHasher,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateCreateUserRequest(request, out var role, out var normalizedUsername, out var normalizedEmail);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var username = request.Username.Trim();
        var fullName = request.FullName.Trim();
        var email = request.Email.Trim();
        if (await db.Users.AnyAsync(
                user => user.NormalizedUsername == normalizedUsername || user.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Username or email already exists."
            });
        }

        var user = new UserAccount
        {
            Username = username,
            NormalizedUsername = normalizedUsername,
            FullName = fullName,
            Email = email,
            NormalizedEmail = normalizedEmail,
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Username or email already exists."
            });
        }

        return Results.Created($"/api/users/{user.Id}", ToResponse(user));
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid id,
        ChangeUserStatusRequest request,
        IdentityDbContext db,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (user.Role == UserRole.Admin && user.IsActive && !request.IsActive && await IsLastActiveAdminAsync(db, cancellationToken))
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The last active administrator cannot be disabled."
            });
        }

        if (TryGetUserId(principal, out var actorId) && actorId == id && !request.IsActive)
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "You cannot disable your own account."
            });
        }

        user.IsActive = request.IsActive;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ChangeRoleAsync(
        Guid id,
        ChangeUserRoleRequest request,
        IdentityDbContext db,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var newRole) || !Enum.IsDefined(newRole))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = ["Role must be Admin, Creator, or Approver."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var user = await db.Users.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (TryGetUserId(principal, out var actorId) && actorId == id && newRole != UserRole.Admin)
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "You cannot remove the administrator role from your own account."
            });
        }

        if (user.Role == UserRole.Admin && user.IsActive && newRole != UserRole.Admin && await IsLastActiveAdminAsync(db, cancellationToken))
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The last active administrator cannot be demoted."
            });
        }

        user.Role = newRole;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ValidateApproversAsync(
        HttpRequest httpRequest,
        ValidateApproversRequest request,
        IdentityDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var configuredKey = configuration["InternalServices:ApiKey"];
        var suppliedKey = httpRequest.Headers["X-Service-Key"].FirstOrDefault();
        if (!HasValidServiceKey(configuredKey, suppliedKey))
        {
            return Results.Unauthorized();
        }

        var approverIds = request.ApproverIdsInOrder ?? [];
        if (approverIds.Count is < 1 or > 3 || approverIds.Distinct().Count() != approverIds.Count)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["approverIdsInOrder"] = ["Choose between one and three different approvers."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var activeApproverIds = await db.Users.AsNoTracking()
            .Where(user => approverIds.Contains(user.Id) && user.IsActive && user.Role == UserRole.Approver)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        if (activeApproverIds.Count != approverIds.Count)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["approverIdsInOrder"] = ["One or more selected accounts are not active approvers."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Ok(new { valid = true });
    }

    private static Dictionary<string, string[]> ValidateCreateUserRequest(
        CreateUserRequest request,
        out UserRole role,
        out string normalizedUsername,
        out string normalizedEmail)
    {
        var errors = new Dictionary<string, string[]>();
        var username = request.Username?.Trim() ?? string.Empty;
        var fullName = request.FullName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;

        if (!UsernamePattern().IsMatch(username))
        {
            errors["username"] = ["Username must be 3-64 characters and contain only letters, numbers, dots, underscores, or hyphens."];
        }

        if (fullName.Length is < 1 or > 160)
        {
            errors["fullName"] = ["Full name is required and must be at most 160 characters."];
        }

        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            errors["email"] = ["Enter a valid email address of at most 254 characters."];
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 12 || request.Password.Length > 256)
        {
            errors["password"] = ["Password must contain between 12 and 256 characters."];
        }

        if (!Enum.TryParse(request.Role, ignoreCase: true, out role) || !Enum.IsDefined(role))
        {
            role = default;
            errors["role"] = ["Role must be Admin, Creator, or Approver."];
        }

        normalizedUsername = UserAccount.Normalize(username);
        normalizedEmail = UserAccount.Normalize(email);
        return errors;
    }

    private static async Task<bool> IsLastActiveAdminAsync(IdentityDbContext db, CancellationToken cancellationToken)
    {
        return await db.Users.CountAsync(
            user => user.IsActive && user.Role == UserRole.Admin,
            cancellationToken) <= 1;
    }

    private static bool HasValidServiceKey(string? configuredKey, string? suppliedKey)
    {
        if (string.IsNullOrEmpty(configuredKey) || string.IsNullOrEmpty(suppliedKey))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(configuredKey);
        var supplied = Encoding.UTF8.GetBytes(suppliedKey);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        return Guid.TryParse(principal.FindFirstValue("sub"), out userId);
    }

    private static IResult InvalidCredentials() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid username/email or password.");

    private static UserResponse ToResponse(UserAccount user) => new(
        user.Id,
        user.Username,
        user.FullName,
        user.Email,
        user.Role.ToString(),
        user.IsActive,
        user.CreatedAtUtc);

    [GeneratedRegex("^[A-Za-z0-9._-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
