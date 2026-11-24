using System.Text;
using Document.Api.Domain;
using Document.Api.Endpoints;
using Document.Api.Infrastructure;
using Document.Api.Security;
using Document.Api.Storage;
using IntegrationContracts.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;

var builder = WebApplication.CreateBuilder(args);

var jwt = builder.Configuration.GetSection("Jwt").Get<DocumentJwtOptions>()
    ?? throw new InvalidOperationException("The Jwt configuration section is required.");
if (string.IsNullOrWhiteSpace(jwt.Issuer)
    || string.IsNullOrWhiteSpace(jwt.Audience)
    || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    throw new InvalidOperationException("Jwt issuer, audience, and a signing key of at least 32 bytes are required.");
}

var connectionString = builder.Configuration.GetConnectionString("DocumentDb")
    ?? throw new InvalidOperationException("ConnectionStrings:DocumentDb is required.");
var storage = builder.Configuration.GetSection(DocumentStorageOptions.SectionName).Get<DocumentStorageOptions>()
    ?? throw new InvalidOperationException("Minio configuration is required.");
if (string.IsNullOrWhiteSpace(storage.Endpoint) || string.IsNullOrWhiteSpace(storage.AccessKey)
    || string.IsNullOrWhiteSpace(storage.SecretKey) || string.IsNullOrWhiteSpace(storage.Bucket))
{
    throw new InvalidOperationException("MinIO endpoint, credentials, and bucket are required.");
}

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 10 * 1024 * 1024 + 64 * 1024);
builder.Services.AddDbContext<DocumentDbContext>(options => options.UseNpgsql(connectionString));
var identityUrl = builder.Configuration["Services:IdentityUrl"] ?? "http://localhost:5101/";
builder.Services.AddHttpClient("Identity", client => client.BaseAddress = new Uri(identityUrl.EndsWith('/') ? identityUrl : $"{identityUrl}/"));
builder.Services.AddSingleton(storage);
builder.Services.AddMinio(client => client
    .WithEndpoint(storage.Endpoint)
    .WithCredentials(storage.AccessKey, storage.SecretKey)
    .WithSSL(storage.UseSsl)
    .Build());
builder.Services.AddScoped<IPrivateObjectStore, MinioPrivateObjectStore>();
var rabbitMqOptions = builder.Services.AddRabbitMqConnection(builder.Configuration);
if (string.IsNullOrWhiteSpace(rabbitMqOptions.QueueName) || rabbitMqOptions.RoutingKeys.Length == 0)
{
    throw new InvalidOperationException("RabbitMq queue and routing keys are required for Document.Api.");
}
builder.Services.AddScoped<IOutboxMessageStore, DocumentOutboxMessageStore>();
builder.Services.AddScoped<IIntegrationEventHandler, DocumentResultEventHandler>();
builder.Services.AddHostedService<OutboxRelayWorker>();
builder.Services.AddRabbitMqConsumer();
builder.Services.AddHostedService<DocumentDatabaseInitializer>();

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CreatorOnly", policy => policy.RequireRole(DocumentRoles.Creator))
    .AddPolicy("DocumentReader", policy => policy.RequireRole(DocumentRoles.Creator, DocumentRoles.Approver));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live").WithName("Liveness");
app.MapGet("/health/ready", async (
    DocumentDbContext db,
    IPrivateObjectStore objectStore,
    CancellationToken cancellationToken) =>
{
    var databaseReady = await db.Database.CanConnectAsync(cancellationToken);
    var objectStoreReady = databaseReady && await objectStore.IsBucketReadyAsync(cancellationToken);
    return databaseReady && objectStoreReady
        ? Results.Ok(new { status = "Ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).WithName("Readiness");
app.MapDocumentEndpoints();

app.Run();

public sealed class DocumentJwtOptions
{
    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string SigningKey { get; init; } = string.Empty;
}

public partial class Program { }
