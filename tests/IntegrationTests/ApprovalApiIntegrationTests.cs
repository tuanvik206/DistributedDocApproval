extern alias ApprovalApi;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ApprovalProgram = ApprovalApi::Program;
using ApprovalWorkflow = ApprovalApi::Approval.Api.Domain.ApprovalWorkflow;
using ApprovalStep = ApprovalApi::Approval.Api.Domain.ApprovalStep;
using ApprovalStepStatus = ApprovalApi::Approval.Api.Domain.ApprovalStepStatus;
using ApprovalDbContext = ApprovalApi::Approval.Api.Infrastructure.ApprovalDbContext;
using CreateDecisionRequest = ApprovalApi::Approval.Api.Contracts.CreateDecisionRequest;
using WorkflowResponse = ApprovalApi::Approval.Api.Contracts.WorkflowResponse;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

[CollectionDefinition(nameof(ApprovalApiIntegrationCollection), DisableParallelization = true)]
public sealed class ApprovalApiIntegrationCollection { }

[Collection(nameof(ApprovalApiIntegrationCollection))]
public sealed class ApprovalApiIntegrationTests : IAsyncLifetime
{
    private const string Issuer = "DistributedDocApproval.Identity.Tests";
    private const string Audience = "DistributedDocApproval.Tests";
    private const string SigningKey = "integration-test-signing-key-with-at-least-32-bytes";
    private const string ApprovalConnectionStringVariable = "ConnectionStrings__ApprovalDb";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("ApprovalIntegration")
        .WithUsername("approval_test")
        .WithPassword("Integration-Only-935!")
        .Build();
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
    private WebApplicationFactory<ApprovalProgram> _factory = null!;
    private HttpClient _client = null!;
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        SetEnvironment(ApprovalConnectionStringVariable, _connectionString);
        SetEnvironment("Jwt__Issuer", Issuer);
        SetEnvironment("Jwt__Audience", Audience);
        SetEnvironment("Jwt__SigningKey", SigningKey);
        SetEnvironment("RabbitMq__Host", "127.0.0.1");

        _factory = new WebApplicationFactory<ApprovalProgram>();
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
    public async Task Decisions_are_sequential_authorized_and_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var creatorId = Guid.NewGuid();
        var firstApproverId = Guid.NewGuid();
        var secondApproverId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var firstStepId = Guid.NewGuid();
        var secondStepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = CreateDbContext())
        {
            var workflow = new ApprovalWorkflow
            {
                Id = workflowId,
                DocumentId = documentId,
                SubmissionId = Guid.NewGuid(),
                CreatorId = creatorId,
                Title = "Sequential approval integration test",
                CreatedAtUtc = now,
                Steps =
                [
                    new ApprovalStep { Id = firstStepId, WorkflowId = workflowId, StepOrder = 1, ApproverId = firstApproverId, Status = ApprovalStepStatus.Active },
                    new ApprovalStep { Id = secondStepId, WorkflowId = workflowId, StepOrder = 2, ApproverId = secondApproverId, Status = ApprovalStepStatus.Waiting }
                ]
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(cancellationToken);
        }

        using var secondApproverRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{secondStepId}/decisions",
            "Approver",
            secondApproverId);
        secondApproverRequest.Content = JsonContent.Create(new CreateDecisionRequest("Approve", "Too early"));
        using var tooEarly = await _client.SendAsync(secondApproverRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

        using var firstApproverRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{firstStepId}/decisions",
            "Approver",
            firstApproverId);
        firstApproverRequest.Content = JsonContent.Create(new CreateDecisionRequest("Approve", "Looks good"));
        using var firstDecision = await _client.SendAsync(firstApproverRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstDecision.StatusCode);

        using var repeatedFirstDecisionRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{firstStepId}/decisions",
            "Approver",
            firstApproverId);
        repeatedFirstDecisionRequest.Content = JsonContent.Create(new CreateDecisionRequest("Approve", "Duplicate"));
        using var repeatedFirstDecision = await _client.SendAsync(repeatedFirstDecisionRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, repeatedFirstDecision.StatusCode);

        using var missingReasonRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{secondStepId}/decisions",
            "Approver",
            secondApproverId);
        missingReasonRequest.Content = JsonContent.Create(new CreateDecisionRequest("Reject", " "));
        using var missingReason = await _client.SendAsync(missingReasonRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingReason.StatusCode);

        using var rejectRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{secondStepId}/decisions",
            "Approver",
            secondApproverId);
        rejectRequest.Content = JsonContent.Create(new CreateDecisionRequest("Reject", "Please attach the budget"));
        using var rejection = await _client.SendAsync(rejectRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);

        using var creatorHistoryRequest = CreateRequest(
            HttpMethod.Get,
            $"/api/approvals/documents/{documentId}",
            "Creator",
            creatorId);
        using var creatorHistoryResponse = await _client.SendAsync(creatorHistoryRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, creatorHistoryResponse.StatusCode);
        var history = await creatorHistoryResponse.Content.ReadFromJsonAsync<WorkflowResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(history);
        Assert.Equal("Rejected", history.Status);
        Assert.Equal(["Approved", "Rejected"], history.Steps.Select(step => step.Status));
        Assert.Equal("Please attach the budget", history.Steps[1].Decision?.Comment);

        using var adminRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{firstStepId}/decisions",
            "Admin",
            Guid.NewGuid());
        adminRequest.Content = JsonContent.Create(new CreateDecisionRequest("Approve", string.Empty));
        using var adminResponse = await _client.SendAsync(adminRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, adminResponse.StatusCode);
    }

    [Fact]
    public async Task Concurrent_decisions_commit_exactly_one_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var creatorId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            db.Workflows.Add(new ApprovalWorkflow
            {
                Id = workflowId,
                DocumentId = documentId,
                SubmissionId = Guid.NewGuid(),
                CreatorId = creatorId,
                Title = "Concurrent approval integration test",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Steps =
                [
                    new ApprovalStep
                    {
                        Id = stepId,
                        WorkflowId = workflowId,
                        StepOrder = 1,
                        ApproverId = approverId,
                        Status = ApprovalStepStatus.Active
                    }
                ]
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        using var approveRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{stepId}/decisions",
            "Approver",
            approverId);
        approveRequest.Content = JsonContent.Create(new CreateDecisionRequest("Approve", "Concurrent approve"));
        using var rejectRequest = CreateRequest(
            HttpMethod.Post,
            $"/api/approvals/tasks/{stepId}/decisions",
            "Approver",
            approverId);
        rejectRequest.Content = JsonContent.Create(new CreateDecisionRequest("Reject", "Concurrent reject"));

        var responses = await Task.WhenAll(
            _client.SendAsync(approveRequest, cancellationToken),
            _client.SendAsync(rejectRequest, cancellationToken));
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

            using var db = CreateDbContext();
            var history = await db.Workflows
                .Include(workflow => workflow.Steps)
                .ThenInclude(step => step.Decision)
                .SingleAsync(workflow => workflow.Id == workflowId, cancellationToken);
            var winningDecision = history.Steps.Single().Decision;
            Assert.NotNull(winningDecision);
            var expectedStatus = winningDecision.Decision.ToString() == "Approve" ? "Approved" : "Rejected";
            Assert.Equal(expectedStatus, history.Status.ToString());
            Assert.Equal(expectedStatus, history.Steps.Single().Status.ToString());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private ApprovalDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ApprovalDbContext>().UseNpgsql(_connectionString).Options);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string role, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role, userId));
        return request;
    }

    private static string CreateToken(string role, Guid userId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim("sub", userId.ToString()), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private void SetEnvironment(string key, string value)
    {
        _previousEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }
}
