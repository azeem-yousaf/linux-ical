using System.Net;
using System.Text;
using ICloudCalendar.Infrastructure.CalDav;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class ICloudSafeRedirectHandlerTests
{
    [Fact]
    public async Task SendAsyncPreservesWebDavMethodBodyAndHeadersAcrossAllowedRedirect()
    {
        var inner = new RecordingHandler((requestNumber, _) => requestNumber == 1
            ? Redirect("https://p01-caldav.icloud.com/123/calendars/")
            : new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("<multistatus />")
            });
        using var client = new HttpClient(new ICloudSafeRedirectHandler(inner));
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "https://caldav.icloud.com/")
        {
            Content = new StringContent("<propfind />", Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", "1");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        inner.Requests.Count.ShouldBe(2);
        inner.Requests[1].Uri.ShouldBe(new Uri("https://p01-caldav.icloud.com/123/calendars/"));
        inner.Requests[1].Method.ShouldBe("PROPFIND");
        inner.Requests[1].Body.ShouldBe("<propfind />");
        inner.Requests[1].Depth.ShouldBe("1");
    }

    [Fact]
    public async Task SendAsyncRejectsExternalRedirectBeforeSecondRequest()
    {
        var inner = new RecordingHandler((_, _) => Redirect("https://calendar-collector.example/steal"));
        using var client = new HttpClient(new ICloudSafeRedirectHandler(inner));

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync("https://caldav.icloud.com/"));

        exception.Message.ShouldContain("credentials were not sent");
        inner.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsyncRejectsUntrustedInitialUriWithoutNetworkAccess()
    {
        var inner = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new ICloudSafeRedirectHandler(inner));

        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("https://example.com/calendar"));

        inner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsyncCapsAllowedRedirectChain()
    {
        var inner = new RecordingHandler((_, _) => Redirect("https://p01-caldav.icloud.com/loop"));
        using var client = new HttpClient(new ICloudSafeRedirectHandler(inner));

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync("https://caldav.icloud.com/"));

        exception.Message.ShouldContain("redirect limit");
        inner.Requests.Count.ShouldBe(6);
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.TemporaryRedirect)
    {
        Headers = { Location = new Uri(location) }
    };

    private sealed record RecordedRequest(Uri? Uri, string Method, string? Body, string? Depth);

    private sealed class RecordingHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri,
                request.Method.Method,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Depth", out var values) ? values.Single() : null));
            return responseFactory(Requests.Count, request);
        }
    }
}
