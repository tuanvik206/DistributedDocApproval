extern alias DocumentApi;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DocumentProgram = DocumentApi::Program;
using DocumentResponse = DocumentApi::Document.Api.Contracts.DocumentResponse;
using IPrivateObjectStore = DocumentApi::Document.Api.Storage.IPrivateObjectStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

[CollectionDefinition(nameof(DocumentApiIntegrationCollection), DisableParallelization = true)]
public sealed class DocumentApiIntegrationCollection { }

[Collection(nameof(DocumentApiIntegrationCollection))]
public sealed class DocumentApiIntegrationTests : IAsyncLifetime
{
    private const string Issuer = "DistributedDocApproval.Identity.Tests";
    private const string Audience = "DistributedDocApproval.Tests";
    private const string SigningKey = "integration-test-signing-key-with-at-least-32-bytes";
    private const string ConnectionStringVariable = "ConnectionStrings__DocumentDb";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("DocumentIntegration")
        .WithUsername("document_test")
        .WithPassword("Integration-Only-937!")
        .Build();
    private readonly InMemoryPrivateObjectStore _objectStore = new();
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
    private WebApplicationFactory<DocumentProgram> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        SetEnvironment(ConnectionStringVariable, _postgres.GetConnectionString());
        SetEnvironment("Jwt__Issuer", Issuer);
        SetEnvironment("Jwt__Audience", Audience);
        SetEnvironment("Jwt__SigningKey", SigningKey);
        SetEnvironment("Minio__Endpoint", "127.0.0.1:9000");
        SetEnvironment("Minio__AccessKey", "document_test");
        SetEnvironment("Minio__SecretKey", "integration-test-object-store-secret");
        SetEnvironment("Minio__Bucket", "document-test");
        SetEnvironment("RabbitMq__Host", "127.0.0.1");

        _factory = new WebApplicationFactory<DocumentProgram>()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrivateObjectStore>();
                services.AddSingleton<IPrivateObjectStore>(_objectStore);
            }));
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
    public async Task Files_are_private_and_downloads_return_the_uploaded_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var creatorId = Guid.NewGuid();
        var otherCreatorId = Guid.NewGuid();
        var fileBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");

        using var uploadRequest = CreateRequest(HttpMethod.Post, "/api/documents", "Creator", creatorId);
        using var uploadContent = CreateUpload("Budget review", "Private PDF", "budget.pdf", fileBytes, "application/pdf");
        uploadRequest.Content = uploadContent;
        using var uploadResponse = await _client.SendAsync(uploadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var document = await uploadResponse.Content.ReadFromJsonAsync<DocumentResponse>(cancellationToken: cancellationToken);
        Assert.NotNull(document);
        Assert.Equal("Draft", document.Status);
        Assert.Equal(fileBytes.LongLength, document.SizeBytes);

        using var ownerReadRequest = CreateRequest(HttpMethod.Get, $"/api/documents/{document.Id}", "Creator", creatorId);
        using var ownerRead = await _client.SendAsync(ownerReadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);

        using var ownerDownloadRequest = CreateRequest(HttpMethod.Get, $"/api/documents/{document.Id}/download", "Creator", creatorId);
        using var ownerDownload = await _client.SendAsync(ownerDownloadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
        Assert.Equal(fileBytes, await ownerDownload.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal("application/pdf", ownerDownload.Content.Headers.ContentType?.MediaType);

        using var otherReadRequest = CreateRequest(HttpMethod.Get, $"/api/documents/{document.Id}", "Creator", otherCreatorId);
        using var otherRead = await _client.SendAsync(otherReadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherRead.StatusCode);

        using var otherDownloadRequest = CreateRequest(HttpMethod.Get, $"/api/documents/{document.Id}/download", "Creator", otherCreatorId);
        using var otherDownload = await _client.SendAsync(otherDownloadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherDownload.StatusCode);

        using var otherListRequest = CreateRequest(HttpMethod.Get, "/api/documents", "Creator", otherCreatorId);
        using var otherListResponse = await _client.SendAsync(otherListRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, otherListResponse.StatusCode);
        var otherDocuments = await otherListResponse.Content.ReadFromJsonAsync<List<DocumentResponse>>(cancellationToken: cancellationToken);
        Assert.NotNull(otherDocuments);
        Assert.DoesNotContain(otherDocuments, item => item.Id == document.Id);

        using var otherUpdateRequest = CreateRequest(HttpMethod.Put, $"/api/documents/{document.Id}", "Creator", otherCreatorId);
        otherUpdateRequest.Content = JsonContent.Create(new { title = "Stolen", description = "No access", expectedVersion = 1 });
        using var otherUpdate = await _client.SendAsync(otherUpdateRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherUpdate.StatusCode);

        using var invalidUploadRequest = CreateRequest(HttpMethod.Post, "/api/documents", "Creator", creatorId);
        using var invalidUploadContent = CreateUpload("Spoofed executable", "Invalid file", "payload.pdf", "MZnot-a-pdf"u8.ToArray(), "application/pdf");
        invalidUploadRequest.Content = invalidUploadContent;
        using var invalidUpload = await _client.SendAsync(invalidUploadRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpload.StatusCode);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string role, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role, userId));
        return request;
    }

    private static MultipartFormDataContent CreateUpload(
        string title,
        string description,
        string fileName,
        byte[] bytes,
        string contentType)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(title), "title");
        content.Add(new StringContent(description), "description");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return content;
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

    private sealed class InMemoryPrivateObjectStore : IPrivateObjectStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> IsBucketReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task PutAsync(string objectKey, Stream content, long sizeBytes, string contentType, CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            _objects[objectKey] = buffer.ToArray();
        }

        public Task<Stream> GetAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_objects[objectKey], writable: false));

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            _objects.TryRemove(objectKey, out _);
            return Task.CompletedTask;
        }
    }
}
