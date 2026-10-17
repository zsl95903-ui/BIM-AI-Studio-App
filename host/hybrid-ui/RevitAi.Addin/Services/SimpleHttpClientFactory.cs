using System.Net.Http;
using RevitAi.Abstractions.Network;

namespace RevitAi.Addin.Services;

// Minimal IHttpClientFactory for the explicit-fetch web tool.
internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    private readonly List<HttpClient> _clients = [];

    public HttpClient CreateClient(string? baseAddress = null, int timeoutSeconds = 30)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 30 : timeoutSeconds)
        };
        if (!string.IsNullOrWhiteSpace(baseAddress))
        {
            client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
        }

        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}

