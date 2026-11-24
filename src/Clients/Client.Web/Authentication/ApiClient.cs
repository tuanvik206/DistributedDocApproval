using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace Client.Web.Authentication;

public sealed class ApiClient(IHttpClientFactory httpClientFactory, AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<HttpResponseMessage> SendAsync(
        string service,
        HttpMethod method,
        string path,
        HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var accessToken = state.User.FindFirst("access_token")?.Value;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The current session has no access token.");
        }

        var client = httpClientFactory.CreateClient(service);
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<T?> GetAsync<T>(string service, string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(service, HttpMethod.Get, path, cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    public Task<HttpResponseMessage> SendJsonAsync<T>(string service, HttpMethod method, string path, T body, CancellationToken cancellationToken = default) =>
        SendAsync(service, method, path, JsonContent.Create(body), cancellationToken);
}
