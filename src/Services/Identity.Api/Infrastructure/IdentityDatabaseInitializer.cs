using Identity.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Identity.Api.Infrastructure;

public sealed class IdentityDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<IdentityDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        logger.LogInformation("Applying Identity database migrations");
        await db.Database.MigrateAsync(cancellationToken);

        var seedOptions = configuration.GetSection("SeedUsers").Get<SeedUsersOptions>() ?? new SeedUsersOptions();
        if (!seedOptions.Enabled)
        {
            logger.LogInformation("Demo user seeding is disabled");
            return;
        }

        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();
        var seedUsers = new[]
        {
            new SeedUser(seedOptions.Admin, UserRole.Admin),
            new SeedUser(seedOptions.Creator, UserRole.Creator),
            new SeedUser(seedOptions.Approver1, UserRole.Approver),
            new SeedUser(seedOptions.Approver2, UserRole.Approver)
        };

        foreach (var seed in seedUsers)
        {
            ValidateSeed(seed);
            var normalizedUsername = UserAccount.Normalize(seed.Settings.Username);
            var normalizedEmail = UserAccount.Normalize(seed.Settings.Email);
            var exists = await db.Users.AnyAsync(
                user => user.NormalizedUsername == normalizedUsername || user.NormalizedEmail == normalizedEmail,
                cancellationToken);
            if (exists)
            {
                continue;
            }

            var user = new UserAccount
            {
                Username = seed.Settings.Username.Trim(),
                NormalizedUsername = normalizedUsername,
                FullName = seed.Settings.FullName.Trim(),
                Email = seed.Settings.Email.Trim(),
                NormalizedEmail = normalizedEmail,
                Role = seed.Role,
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            user.PasswordHash = passwordHasher.HashPassword(user, seed.Settings.Password);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Configured demo users are present in IdentityDB");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateSeed(SeedUser seed)
    {
        if (string.IsNullOrWhiteSpace(seed.Settings.Username)
            || string.IsNullOrWhiteSpace(seed.Settings.FullName)
            || string.IsNullOrWhiteSpace(seed.Settings.Email)
            || string.IsNullOrWhiteSpace(seed.Settings.Password)
            || seed.Settings.Password.Length < 12)
        {
            throw new InvalidOperationException(
                $"SeedUsers is enabled, but the {seed.Role} seed account is incomplete or its password is shorter than 12 characters.");
        }
    }

    private sealed record SeedUser(SeedUserSettings Settings, UserRole Role);
}

public sealed class SeedUsersOptions
{
    public bool Enabled { get; init; }

    public SeedUserSettings Admin { get; init; } = new();

    public SeedUserSettings Creator { get; init; } = new();

    public SeedUserSettings Approver1 { get; init; } = new();

    public SeedUserSettings Approver2 { get; init; } = new();
}

public sealed class SeedUserSettings
{
    public string Username { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
