using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Identity.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

[CollectionDefinition(nameof(IdentityApiIntegrationCollection), DisableParallelization = true)]
public sealed class IdentityApiIntegrationCollection { }

[Collection(nameof(IdentityApiIntegrationCollection))]
public sealed class IdentityApiIntegrationTests : IAsyncLifetime
{
    private const string AdminPassword = "Admin-Integration-934!";
    private const string CreatorPassword = "Creator-Integration-934!";
    private const string ApproverOnePassword = "Approver-One-Integration-934!";
    private const string ApproverTwoPassword = "Approver-Two-Integration-934!";
    private const string ServiceApiKey = "integration-test-service-key";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("IdentityIntegration")
        .WithUsername("identity_test")
        .WithPassword("Integration-Only-934!")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:IdentityDb"] = _postgres.GetConnectionString(),
            ["Jwt:Issuer"] = "DistributedDocApproval.Identity.Tests",
            ["Jwt:Audience"] = "DistributedDocApproval.Tests",
            ["Jwt:SigningKey"] = "integration-test-signing-key-with-at-least-32-bytes",
            ["InternalServices:ApiKey"] = ServiceApiKey,
            ["SeedUsers:Enabled"] = "true",
            ["SeedUsers:Admin:Username"] = "admin",
            ["SeedUsers:Admin:FullName"] = "Integration Administrator",
            ["SeedUsers:Admin:Email"] = "admin@example.test",
            ["SeedUsers:Admin:Password"] = AdminPassword,
            ["SeedUsers:Creator:Username"] = "creator",
            ["SeedUsers:Creator:FullName"] = "Integration Creator",
            ["SeedUsers:Creator:Email"] = "creator@example.test",
            ["SeedUsers:Creator:Password"] = CreatorPassword,
            ["SeedUsers:Approver1:Username"] = "approver1",
            ["SeedUsers:Approver1:FullName"] = "Integration Approver One",
            ["SeedUsers:Approver1:Email"] = "approver1@example.test",
            ["SeedUsers:Approver1:Password"] = ApproverOnePassword,
            ["SeedUsers:Approver2:Username"] = "approver2",
            ["SeedUsers:Approver2:FullName"] = "Integration Approver Two",
            ["SeedUsers:Approver2:Email"] = "approver2@example.test",
            ["SeedUsers:Approver2:Password"] = ApproverTwoPassword
        };

        foreach (var (key, value) in settings)
        {
            var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
            _previousEnvironment.TryAdd(environmentKey, Environment.GetEnvironmentVariable(environmentKey));
            Environment.SetEnvironmentVariable(environmentKey, value);
        }

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        using var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        foreach (var (key, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task Login_uses_username_or_email_and_enforces_role_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.test", AdminPassword),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var admin = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(admin);
        Assert.Equal("Admin", admin.User.Role);

        using var usersWithoutToken = await _client.GetAsync("/api/users", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, usersWithoutToken.StatusCode);

        var creatorToken = await LoginAsync("creator", CreatorPassword, cancellationToken);
        using var usersAsCreator = await GetWithBearerAsync("/api/users", creatorToken, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, usersAsCreator.StatusCode);

        using var approversAsCreator = await GetWithBearerAsync("/api/users/approvers", creatorToken, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, approversAsCreator.StatusCode);
        var approvers = await approversAsCreator.Content.ReadFromJsonAsync<List<UserResponse>>(cancellationToken: cancellationToken);
        Assert.NotNull(approvers);
        Assert.Equal(2, approvers.Count);
        Assert.All(approvers, approver => Assert.Equal("Approver", approver.Role));
    }

    [Fact]
    public async Task Internal_approver_validation_requires_service_key_and_active_unique_approvers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var creatorToken = await LoginAsync("creator", CreatorPassword, cancellationToken);
        using var approversResponse = await GetWithBearerAsync("/api/users/approvers", creatorToken, cancellationToken);
        var approvers = await approversResponse.Content.ReadFromJsonAsync<List<UserResponse>>(cancellationToken: cancellationToken);
        Assert.NotNull(approvers);
        var approverIds = approvers.Select(approver => approver.Id).ToArray();

        using var noKey = await _client.PostAsJsonAsync(
            "/internal/approvers/validate",
            new ValidateApproversRequest(approverIds),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var validRequest = new HttpRequestMessage(HttpMethod.Post, "/internal/approvers/validate")
        {
            Content = JsonContent.Create(new ValidateApproversRequest(approverIds))
        };
        validRequest.Headers.Add("X-Service-Key", ServiceApiKey);
        using var validResponse = await _client.SendAsync(validRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

        using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/internal/approvers/validate")
        {
            Content = JsonContent.Create(new ValidateApproversRequest([approverIds[0], approverIds[0]]))
        };
        duplicateRequest.Headers.Add("X-Service-Key", ServiceApiKey);
        using var duplicateResponse = await _client.SendAsync(duplicateRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_can_create_users_but_cannot_disable_the_last_active_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminToken = await LoginAsync("admin", AdminPassword, cancellationToken);
        var newUser = new CreateUserRequest(
            "new.approver",
            "New Integration Approver",
            "new.approver@example.test",
            "New-Approver-Integration-934!",
            "Approver");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(newUser)
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<UserResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(created);
        Assert.Equal("Approver", created.Role);

        var adminMe = await GetCurrentUserAsync(adminToken, cancellationToken);
        using var disableLastAdmin = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{adminMe.Id}/status")
        {
            Content = JsonContent.Create(new ChangeUserStatusRequest(false))
        };
        disableLastAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var conflict = await _client.SendAsync(disableLastAdmin, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var selfRoleChange = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{adminMe.Id}/role")
        {
            Content = JsonContent.Create(new ChangeUserRoleRequest("Creator"))
        };
        selfRoleChange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var selfRoleConflict = await _client.SendAsync(selfRoleChange, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, selfRoleConflict.StatusCode);
    }

    private async Task<string> LoginAsync(string usernameOrEmail, string password, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(usernameOrEmail, password),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(login);
        return login.AccessToken;
    }

    private async Task<UserResponse> GetCurrentUserAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await GetWithBearerAsync("/api/users/me", token, cancellationToken);
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(user);
        return user;
    }

    private async Task<HttpResponseMessage> GetWithBearerAsync(
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, cancellationToken);
    }
}
