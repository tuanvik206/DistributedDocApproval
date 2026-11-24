using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using Document.Api.Contracts;
using Document.Api.Domain;
using Document.Api.Infrastructure;
using Document.Api.Security;
using Document.Api.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minio.Exceptions;
using IntegrationContracts;

namespace Document.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup("/api/documents").RequireAuthorization("DocumentReader");
        documents.MapPost("", CreateAsync)
            .RequireAuthorization("CreatorOnly")
            .DisableAntiforgery();
        documents.MapGet("", ListAsync);
        documents.MapGet("/{id:guid}", GetAsync);
        documents.MapGet("/{id:guid}/download", DownloadAsync);
        documents.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("CreatorOnly");
        documents.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("CreatorOnly");
        documents.MapPost("/{id:guid}/submit", SubmitAsync).RequireAuthorization("CreatorOnly");
        return endpoints;
    }

    private static async Task<IResult> SubmitAsync(
        Guid id,
        SubmitDocumentRequest request,
        HttpContext httpContext,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var creatorId))
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

        var document = await db.Documents
            .Include(item => item.Readers)
            .SingleOrDefaultAsync(item => item.Id == id && item.CreatorId == creatorId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        if (document.Status != DocumentStatus.Draft)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Only draft documents can be submitted." });
        }

        if (request.ExpectedVersion != document.Version)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The document was changed by another request." });
        }

        var serviceKey = configuration["InternalServices:ApiKey"];
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Approver validation is not configured.");
        }

        HttpResponseMessage validationResponse;
        try
        {
            var client = httpClientFactory.CreateClient("Identity");
            using var validationRequest = new HttpRequestMessage(HttpMethod.Post, "internal/approvers/validate")
            {
                Content = JsonContent.Create(new { approverIdsInOrder = approverIds })
            };
            validationRequest.Headers.Add("X-Service-Key", serviceKey);
            validationResponse = await client.SendAsync(validationRequest, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            loggerFactory.CreateLogger("Document.Api.Submit").LogWarning(exception, "Identity service is unavailable while validating approvers for document {DocumentId}", id);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Approver validation is temporarily unavailable.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("Document.Api.Submit").LogWarning(exception, "Identity service timed out while validating approvers for document {DocumentId}", id);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Approver validation is temporarily unavailable.");
        }

        using (validationResponse)
        {
            if (validationResponse.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["approverIdsInOrder"] = ["One or more selected accounts are not active approvers."]
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (!validationResponse.IsSuccessStatusCode)
            {
                loggerFactory.CreateLogger("Document.Api.Submit").LogWarning(
                    "Identity service returned {StatusCode} while validating approvers for document {DocumentId}",
                    (int)validationResponse.StatusCode,
                    id);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Approver validation is temporarily unavailable.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var submissionId = Guid.NewGuid();
        var correlationId = GetCorrelationId(httpContext);
        var envelope = IntegrationEventEnvelope.Create(
            IntegrationEventTypes.DocumentSubmitted,
            correlationId,
            new DocumentSubmittedV1(id, submissionId, creatorId, document.Title, approverIds));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        document.Status = DocumentStatus.Pending;
        document.SubmissionId = submissionId;
        document.SubmittedAtUtc = now;
        document.Version++;
        foreach (var approverId in approverIds)
        {
            document.Readers.Add(new DocumentReader { DocumentId = id, UserId = approverId });
        }

        db.OutboxMessages.Add(new OutboxMessage
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            RoutingKey = IntegrationRoutingKeys.DocumentSubmitted,
            PayloadJson = JsonSerializer.Serialize(envelope),
            CorrelationId = correlationId,
            CreatedAtUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The document was changed by another request." });
        }

        loggerFactory.CreateLogger("Document.Api.Submit").LogInformation(
            "Submitted document {DocumentId} with submission {SubmissionId}; {ApproverCount} approvers; correlation {CorrelationId}",
            id,
            submissionId,
            approverIds.Count,
            correlationId);

        return Results.Accepted($"/api/documents/{id}", new { documentId = id, submissionId, status = document.Status.ToString(), document.Version });
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest request,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        IPrivateObjectStore objectStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var creatorId))
        {
            return Results.Unauthorized();
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The uploaded request is too large or malformed.");
        }

        var title = form["title"].ToString().Trim();
        var description = form["description"].ToString().Trim();
        var errors = ValidateMetadata(title, description);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var validation = await DocumentFileValidator.ValidateAsync(form.Files.GetFile("file"), cancellationToken);
        if (validation.File is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = [validation.Error ?? "The file is invalid."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var upload = validation.File;
        var objectKey = Guid.NewGuid().ToString("N");
        try
        {
            await using var content = new MemoryStream(upload.Content, writable: false);
            await objectStore.PutAsync(objectKey, content, upload.Content.LongLength, upload.ContentType, cancellationToken);
        }
        catch (MinioException exception)
        {
            loggerFactory.CreateLogger("Document.Api.Upload").LogError(exception, "Could not store uploaded document object {ObjectKey}", objectKey);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Document storage is temporarily unavailable.");
        }

        var document = new DocumentRecord
        {
            CreatorId = creatorId,
            Title = title,
            Description = description,
            OriginalFileName = upload.OriginalFileName,
            ObjectKey = objectKey,
            ContentType = upload.ContentType,
            SizeBytes = upload.Content.LongLength,
            Sha256 = upload.Sha256,
            Status = DocumentStatus.Draft,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1
        };

        db.Documents.Add(document);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            try
            {
                await objectStore.DeleteAsync(objectKey, CancellationToken.None);
            }
            catch (MinioException cleanupException)
            {
                loggerFactory.CreateLogger("Document.Api.Upload").LogError(
                    cleanupException,
                    "Could not clean unreferenced document object {ObjectKey} after metadata save failed",
                    objectKey);
            }

            loggerFactory.CreateLogger("Document.Api.Upload").LogError(exception, "Could not save document metadata for {DocumentId}", document.Id);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The document could not be saved.");
        }

        return Results.Created($"/api/documents/{document.Id}", ToResponse(document));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        DocumentDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var documents = await db.Documents.AsNoTracking()
            .Where(document => document.CreatorId == userId || document.Readers.Any(reader => reader.UserId == userId))
            .OrderByDescending(document => document.CreatedAtUtc)
            .Select(document => ToResponse(document))
            .ToListAsync(cancellationToken);

        return Results.Ok(documents);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var document = await GetReadableDocumentAsync(id, userId, db, cancellationToken);
        return document is null ? Results.NotFound() : Results.Ok(ToResponse(document));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        IPrivateObjectStore objectStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var document = await GetReadableDocumentAsync(id, userId, db, cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        try
        {
            var content = await objectStore.GetAsync(document.ObjectKey, cancellationToken);
            return Results.Stream(content, document.ContentType, document.OriginalFileName);
        }
        catch (MinioException exception)
        {
            loggerFactory.CreateLogger("Document.Api.Download").LogError(exception, "Could not read document object {ObjectKey}", document.ObjectKey);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Document storage is temporarily unavailable.");
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var creatorId))
        {
            return Results.Unauthorized();
        }

        var document = await db.Documents.SingleOrDefaultAsync(
            item => item.Id == id && item.CreatorId == creatorId,
            cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        if (document.Status != DocumentStatus.Draft)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Only draft documents can be edited." });
        }

        if (request.ExpectedVersion != document.Version)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The document was changed by another request." });
        }

        var title = request.Title?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        var errors = ValidateMetadata(title, description);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        document.Title = title;
        document.Description = description;
        document.Version++;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The document was changed by another request." });
        }

        return Results.Ok(ToResponse(document));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        DocumentDbContext db,
        IPrivateObjectStore objectStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var creatorId))
        {
            return Results.Unauthorized();
        }

        var document = await db.Documents.SingleOrDefaultAsync(
            item => item.Id == id && item.CreatorId == creatorId,
            cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        if (document.Status != DocumentStatus.Draft)
        {
            return Results.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Only draft documents can be deleted." });
        }

        db.Documents.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await objectStore.DeleteAsync(document.ObjectKey, cancellationToken);
        }
        catch (MinioException exception)
        {
            loggerFactory.CreateLogger("Document.Api.Delete").LogError(
                exception,
                "Deleted document metadata but could not remove object {ObjectKey}; it will require cleanup",
                document.ObjectKey);
        }

        return Results.NoContent();
    }

    private static Task<DocumentRecord?> GetReadableDocumentAsync(
        Guid id,
        Guid userId,
        DocumentDbContext db,
        CancellationToken cancellationToken) => db.Documents.AsNoTracking()
        .Include(document => document.Readers)
        .SingleOrDefaultAsync(
            document => document.Id == id && (document.CreatorId == userId || document.Readers.Any(reader => reader.UserId == userId)),
            cancellationToken);

    private static Dictionary<string, string[]> ValidateMetadata(string title, string description)
    {
        var errors = new Dictionary<string, string[]>();
        if (title.Length is < 1 or > 200)
        {
            errors["title"] = ["Title is required and must be at most 200 characters."];
        }

        if (description.Length > 2000)
        {
            errors["description"] = ["Description must be at most 2000 characters."];
        }

        return errors;
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out userId);

    private static string GetCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(supplied) || supplied.Length > 128 ? context.TraceIdentifier : supplied.Trim();
    }

    private static DocumentResponse ToResponse(DocumentRecord document) => new(
        document.Id,
        document.CreatorId,
        document.Title,
        document.Description,
        document.OriginalFileName,
        document.ContentType,
        document.SizeBytes,
        document.Sha256,
        document.Status.ToString(),
        document.CreatedAtUtc,
        document.SubmittedAtUtc,
        document.CompletedAtUtc,
        document.Version);
}
