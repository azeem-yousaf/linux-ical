using System.Net;
using System.Net.Http.Headers;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class ICloudSafeRedirectHandler(
    HttpMessageHandler innerHandler,
    AuthenticationHeaderValue? authorization = null) : DelegatingHandler(innerHandler)
{
    private const int MaximumRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await RequestSnapshot.CreateAsync(request, cancellationToken);
        var currentRequest = request;

        for (var redirectCount = 0; ; redirectCount++)
        {
            EnsureAllowed(currentRequest.RequestUri);
            if (authorization is not null)
            {
                currentRequest.Headers.Authorization = authorization;
            }
            var response = await base.SendAsync(currentRequest, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            if (redirectCount >= MaximumRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("iCloud exceeded the safe redirect limit.");
            }

            var location = response.Headers.Location;
            var redirectedUri = location is null || currentRequest.RequestUri is null
                ? null
                : new Uri(currentRequest.RequestUri, location);
            response.Dispose();
            EnsureAllowed(redirectedUri);
            currentRequest = snapshot.CreateRequest(redirectedUri!);
        }
    }

    public static bool IsAllowedICloudUri(Uri? uri) => uri is { IsAbsoluteUri: true }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && (uri.Host.Equals("icloud.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase));

    private static void EnsureAllowed(Uri? uri)
    {
        if (!IsAllowedICloudUri(uri))
        {
            throw new HttpRequestException("iCloud redirected to an untrusted server; credentials were not sent.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers,
        byte[]? Content,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> ContentHeaders)
    {
        public static async Task<RequestSnapshot> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new RequestSnapshot(
                request.Method,
                request.Version,
                request.VersionPolicy,
                request.Headers
                    .Where(header => !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    .Select(header => new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray()))
                    .ToArray(),
                content,
                request.Content?.Headers
                    .Select(header => new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray()))
                    .ToArray() ?? []);
        }

        public HttpRequestMessage CreateRequest(Uri uri)
        {
            var request = new HttpRequestMessage(Method, uri)
            {
                Version = Version,
                VersionPolicy = VersionPolicy
            };
            foreach (var header in Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (Content is not null)
            {
                request.Content = new ByteArrayContent(Content);
                foreach (var header in ContentHeaders)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return request;
        }
    }
}
