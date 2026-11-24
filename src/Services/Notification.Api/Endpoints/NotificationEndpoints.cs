using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Notification.Api.Contracts;
using Notification.Api.Infrastructure;

namespace Notification.Api.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var notifications = endpoints.MapGroup("/api/notifications").RequireAuthorization();
        notifications.MapGet("", GetMineAsync);
        notifications.MapPatch("/{id:guid}/read", SetReadAsync);
        return endpoints;
    }

    private static async Task<IResult> GetMineAsync(
        ClaimsPrincipal principal,
        NotificationDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var items = await db.Notifications.AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Select(notification => new NotificationResponse(
                notification.Id,
                notification.DocumentId,
                notification.Type,
                notification.Message,
                notification.IsRead,
                notification.CreatedAtUtc,
                notification.ReadAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(items);
    }

    private static async Task<IResult> SetReadAsync(
        Guid id,
        ReadNotificationRequest request,
        ClaimsPrincipal principal,
        NotificationDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var notification = await db.Notifications.SingleOrDefaultAsync(
            item => item.Id == id && item.UserId == userId,
            cancellationToken);
        if (notification is null)
        {
            return Results.NotFound();
        }

        notification.IsRead = request.IsRead;
        notification.ReadAtUtc = request.IsRead ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new NotificationResponse(
            notification.Id,
            notification.DocumentId,
            notification.Type,
            notification.Message,
            notification.IsRead,
            notification.CreatedAtUtc,
            notification.ReadAtUtc));
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out userId);
}
