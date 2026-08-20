using ICloudCalendar.Infrastructure.CalDav;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalDavCalendarDiscoveryTests
{
    private static readonly Uri ServiceUri = new("https://caldav.icloud.com/");
    private static readonly Uri ShardUri = new("https://p01-caldav.icloud.com/");
    private static readonly Uri PrincipalUri = new("https://p01-caldav.icloud.com/123/principal/");
    private static readonly Uri HomeUri = new("https://p01-caldav.icloud.com/123/calendars/");
    private readonly ICalDavTransport _transport = Substitute.For<ICalDavTransport>();

    [Fact]
    public async Task DiscoverAsyncFollowsEffectiveShardAndReturnsEventCalendarsOnly()
    {
        _transport.PropFindAsync(ServiceUri, Arg.Any<string>(), 0, Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(207, PrincipalResponse, ShardUri));
        _transport.PropFindAsync(PrincipalUri, Arg.Any<string>(), 0, Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(207, HomeResponse, PrincipalUri));
        _transport.PropFindAsync(HomeUri, Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(207, CalendarsResponse, HomeUri));

        var result = await new CalDavCalendarDiscovery(_transport).DiscoverAsync(ServiceUri);

        result.Count.ShouldBe(2);
        result.Select(item => item.DisplayName).ShouldBe(["Personal", "Work"]);
        result[0].Uri.ShouldBe(new Uri("https://p01-caldav.icloud.com/123/calendars/personal/"));
        result[0].Color.ShouldBe("#AF52DEFF");
        result[1].SyncToken.ShouldBe("https://icloud/sync/work-7");
        result.Select(item => item.Id).ShouldAllBe(item => item.Length == 16);
    }

    [Fact]
    public async Task DiscoverAsyncMapsAuthenticationFailureAndStops()
    {
        _transport.PropFindAsync(ServiceUri, Arg.Any<string>(), 0, Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(401, "Unauthorized"));

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => new CalDavCalendarDiscovery(_transport).DiscoverAsync(ServiceUri));

        exception.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        await _transport.Received(1).PropFindAsync(ServiceUri, Arg.Any<string>(), 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsyncRejectsNonHttpsServiceBeforeNetworkCall()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => new CalDavCalendarDiscovery(_transport).DiscoverAsync(new Uri("http://caldav.example.test")));

        await _transport.DidNotReceiveWithAnyArgs().PropFindAsync(default!, default!, default, default);
    }

    private const string PrincipalResponse = """
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/</d:href><d:propstat><d:prop>
        <d:current-user-principal><d:href>/123/principal/</d:href></d:current-user-principal>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
        """;

    private const string HomeResponse = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/123/principal/</d:href><d:propstat><d:prop>
        <c:calendar-home-set><d:href>/123/calendars/</d:href></c:calendar-home-set>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
        """;

    private const string CalendarsResponse = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/">
          <d:response><d:href>/123/calendars/work/</d:href><d:propstat><d:prop><d:displayname>Work</d:displayname><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set><d:sync-token>https://icloud/sync/work-7</d:sync-token></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/personal/</d:href><d:propstat><d:prop><d:displayname>Personal</d:displayname><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set><a:calendar-color>#AF52DEFF</a:calendar-color></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/inbox/</d:href><d:propstat><d:prop><d:displayname>Inbox</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;
}
