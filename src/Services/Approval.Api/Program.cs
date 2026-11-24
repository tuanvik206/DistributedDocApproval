using System.Text;
using Approval.Api.Endpoints;
using Approval.Api.Infrastructure;
using IntegrationContracts.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var issuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is required.");
var audience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is required.");
var signingKeyText = builder.Configuration["Jwt:SigningKey"] ?? string.Empty;
if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");
}

var connectionString = builder.Configuration.GetConnectionString("ApprovalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ApprovalDb is required.");

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddDbContext<ApprovalDbContext>(options => options.UseNpgsql(connectionString));
var rabbitMqOptions = builder.Services.AddRabbitMqConnection(builder.Configuration);
if (string.IsNullOrWhiteSpace(rabbitMqOptions.QueueName) || rabbitMqOptions.RoutingKeys.Length == 0)
{
    throw new InvalidOperationException("RabbitMq queue and routing keys are required for Approval.Api.");
}
builder.Services.AddScoped<IOutboxMessageStore, ApprovalOutboxMessageStore>();
builder.Services.AddScoped<IIntegrationEventHandler, DocumentSubmittedEventHandler>();
builder.Services.AddHostedService<OutboxRelayWorker>();
builder.Services.AddRabbitMqConsumer();
builder.Services.AddHostedService<ApprovalDatabaseInitializer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText)),
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live").WithName("Liveness");
app.MapGet("/health/ready", async (ApprovalDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken) ? Results.Ok(new { status = "Ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .WithName("Readiness");
app.MapApprovalEndpoints();

app.Run();
