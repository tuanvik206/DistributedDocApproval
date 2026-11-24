namespace Identity.Api.Contracts;

public sealed record LoginRequest(string UsernameOrEmail, string Password);

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc,
    UserResponse User);

public sealed record CreateUserRequest(
    string Username,
    string FullName,
    string Email,
    string Password,
    string Role);

public sealed record ChangeUserStatusRequest(bool IsActive);

public sealed record ChangeUserRoleRequest(string Role);

public sealed record ValidateApproversRequest(IReadOnlyList<Guid> ApproverIdsInOrder);

public sealed record UserResponse(
    Guid Id,
    string Username,
    string FullName,
    string Email,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);
