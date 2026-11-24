using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Client.Web.Authentication;
using Client.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "DistributedDocApproval.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/forbidden";
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();

var serviceUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["identity"] = builder.Configuration["ApiServices:Identity"] ?? "http://localhost:5111/",
    ["document"] = builder.Configuration["ApiServices:Document"] ?? "http://localhost:5112/",
    ["approval"] = builder.Configuration["ApiServices:Approval"] ?? "http://localhost:5113/",
    ["notification"] = builder.Configuration["ApiServices:Notification"] ?? "http://localhost:5114/"
};
foreach (var (name, address) in serviceUrls)
{
    builder.Services.AddHttpClient(name, client => client.BaseAddress = new Uri(address.EndsWith('/') ? address : $"{address}/"));
}
builder.Services.AddScoped<ApiClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapGet("/login", LoginPage).AllowAnonymous();
app.MapPost("/auth/login", LoginAsync).AllowAnonymous();
app.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
app.MapGet("/files/{id:guid}", DownloadDocumentAsync).RequireAuthorization();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

static IResult LoginPage(HttpContext context, IAntiforgery antiforgery)
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        return Results.LocalRedirect("/");
    }

    var tokens = antiforgery.GetAndStoreTokens(context);
    var returnUrl = context.Request.Query["ReturnUrl"].ToString();
    if (!IsLocalPath(returnUrl))
    {
        returnUrl = "/";
    }

    var error = context.Request.Query["error"].ToString();
    var message = error switch
    {
        "invalid" => "Tên đăng nhập/email hoặc mật khẩu không đúng.",
        "unavailable" => "Dịch vụ đăng nhập đang tạm thời không khả dụng.",
        _ => string.Empty
    };
    var safeMessage = HtmlEncoder.Default.Encode(message);
    var formFieldName = HtmlEncoder.Default.Encode(tokens.FormFieldName);
    var requestToken = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var safeReturnUrl = HtmlEncoder.Default.Encode(returnUrl);
    var errorMarkup = string.IsNullOrEmpty(message)
        ? string.Empty
        : $"<div class=\"alert alert-error\" role=\"alert\">{safeMessage}</div>";
    var html = $"""
        <!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Đăng nhập · DocFlow</title><link rel="stylesheet" href="/app.css"></head>
        <body class="login-page"><main class="login-card"><div class="brand-mark">DF</div><p class="eyebrow">DISTRIBUTED DOCUMENT APPROVAL</p>
        <h1>Đăng nhập</h1><p class="muted">Theo dõi và xử lý hồ sơ phê duyệt của bạn.</p>
        {errorMarkup}
        <form method="post" action="/auth/login"><input type="hidden" name="{formFieldName}" value="{requestToken}">
        <input type="hidden" name="returnUrl" value="{safeReturnUrl}">
        <label for="usernameOrEmail">Tên đăng nhập hoặc email</label><input id="usernameOrEmail" name="usernameOrEmail" autocomplete="username" required>
        <label for="password">Mật khẩu</label><input id="password" name="password" type="password" autocomplete="current-password" required>
        <button class="button button-primary button-wide" type="submit">Đăng nhập</button></form>
        <p class="login-footnote">Tài khoản demo được cấu hình trong <code>.env</code>.</p></main></body></html>
        """;
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(html, "text/html; charset=utf-8");
}

static async Task<IResult> LoginAsync(
    HttpContext context,
    IAntiforgery antiforgery,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var usernameOrEmail = form["usernameOrEmail"].ToString().Trim();
        var password = form["password"].ToString();
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrEmpty(password))
        {
            return Results.LocalRedirect("/login?error=invalid");
        }

        var client = httpClientFactory.CreateClient("identity");
        using var response = await client.PostAsJsonAsync(
            "api/auth/login",
            new { usernameOrEmail, password },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Results.LocalRedirect(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "/login?error=invalid"
                : "/login?error=unavailable");
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
        {
            return Results.LocalRedirect("/login?error=unavailable");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, login.User.Id.ToString()),
            new Claim(ClaimTypes.Name, login.User.FullName),
            new Claim(ClaimTypes.Role, login.User.Role),
            new Claim("access_token", login.AccessToken),
            new Claim("user_role", login.User.Role)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            ExpiresUtc = login.ExpiresAtUtc
        };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        context.Response.Headers.CacheControl = "no-store";
        var returnUrl = form["returnUrl"].ToString();
        return IsLocalPath(returnUrl) ? Results.LocalRedirect(returnUrl) : Results.LocalRedirect("/");
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("The login form expired. Reload the page and try again.");
    }
    catch (HttpRequestException)
    {
        return Results.LocalRedirect("/login?error=unavailable");
    }
}

static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery)
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Headers.CacheControl = "no-store";
    return Results.LocalRedirect("/login");
}

static async Task<IResult> DownloadDocumentAsync(
    Guid id,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    var accessToken = context.User.FindFirst("access_token")?.Value;
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Unauthorized();
    }

    using var request = new HttpRequestMessage(HttpMethod.Get, $"api/documents/{id}/download");
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    using var response = await httpClientFactory.CreateClient("document").SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound();
    }
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The document file is temporarily unavailable.");
    }

    var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
    var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
        ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
        ?? $"document-{id:N}";
    context.Response.Headers.CacheControl = "no-store";
    return Results.File(content, contentType, fileName);
}

static bool IsLocalPath(string? path) =>
    !string.IsNullOrWhiteSpace(path)
    && path.StartsWith("/", StringComparison.Ordinal)
    && !path.StartsWith("//", StringComparison.Ordinal)
    && !path.StartsWith("/\\", StringComparison.Ordinal);

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAtUtc, LoginUser User);

public sealed record LoginUser(Guid Id, string Username, string FullName, string Email, string Role, bool IsActive, DateTimeOffset CreatedAtUtc);
