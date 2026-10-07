using System.Net;

namespace MavenOperator.Tests.Unit.Infrastructure;

/// <summary>
/// Scriptable <see cref="HttpMessageHandler"/> for testing components that depend on HttpClient.
/// Register exact-URL routes and/or URL-substring fallbacks; unhandled requests get 404.
/// Every received request is captured in <see cref="Requests"/> (headers included).
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Route that matches when the request URI equals <paramref name="url"/> exactly.</summary>
    public ScriptedHttpHandler WhenExact(string url, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        Add(url, exact: true, responder);

    public ScriptedHttpHandler WhenExact(string url, HttpResponseMessage response) =>
        WhenExact(url, _ => Task.FromResult(response));

    /// <summary>Route that matches when the request URI contains <paramref name="urlContains"/>.</summary>
    public ScriptedHttpHandler WhenUrlContains(string urlContains, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        Add(urlContains, exact: false, responder);

    public ScriptedHttpHandler WhenUrlContains(string urlContains, HttpResponseMessage response) =>
        WhenUrlContains(urlContains, _ => Task.FromResult(response));

    private ScriptedHttpHandler Add(
        string matcher,
        bool exact,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        // Exact routes are normalized to the URL path (query strings ignored);
        // substring needles are kept as-is and matched against the full URI.
        var stored = exact ? PathOf(matcher) : matcher;
        lock (_routes)
        {
            _routes.Add(new Route(stored, exact, responder));
        }

        return this;
    }

    private static string PathOf(string url) => new Uri(url).GetLeftPart(System.UriPartial.Path).TrimEnd('/');

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var fullUri = request.RequestUri.ToString();
        Route? route;
        lock (_routes)
        {
            // Exact routes win over substring routes, regardless of registration order.
            route = _routes.FirstOrDefault(r => r.Exact && string.Equals(PathOf(fullUri), r.Matcher, StringComparison.OrdinalIgnoreCase))
                    ?? _routes.FirstOrDefault(r => !r.Exact && fullUri.Contains(r.Matcher, StringComparison.OrdinalIgnoreCase));
        }

        return route is not null
            ? route.Responder(request)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no scripted response for {fullUri}"),
            });
    }

    private sealed record Route(string Matcher, bool Exact, Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder);
}
