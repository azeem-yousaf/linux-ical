using ICloudCalendar.Infrastructure.CalDav;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalDavCalendarChangeSourceTests
{
    private static readonly Uri CalendarUri = new("https://caldav.icloud.com/calendars/work/");
    private readonly ICalDavTransport _transport = Substitute.For<ICalDavTransport>();
    private readonly ICalendarEndpointResolver _endpoints = Substitute.For<ICalendarEndpointResolver>();
    private readonly ICalendarPayloadParser _parser = Substitute.For<ICalendarPayloadParser>();

    [Fact]
    public async Task GetChangesAsyncParsesUpdatedAndDeletedResources()
    {
        const string payload = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:meeting\nEND:VEVENT\nEND:VCALENDAR";
        var parsedEvent = Event("/work/meeting.ics", "\"etag-2\"");
        _endpoints.Resolve("work").Returns(CalendarUri);
        _parser.Parse("work", "/work/meeting.ics", "\"etag-2\"", payload).Returns([parsedEvent]);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(207, ResponseXml(payload)));

        var page = await Sut().GetChangesAsync("work", "https://icloud/token/1", null, CancellationToken.None);

        page.NextSyncToken.ShouldBe("https://icloud/token/2");
        page.NextPageCursor.ShouldBeNull();
        page.Changes.Count.ShouldBe(2);
        page.Changes[0].RemoteId.ShouldBe("/work/meeting.ics");
        page.Changes[0].Events.ShouldBe([parsedEvent]);
        page.Changes[1].ShouldBe(new CalendarChange("/work/deleted.ics", null));
        await _transport.Received().ReportAsync(
            CalendarUri,
            Arg.Is<string>(body => body.Contains("https://icloud/token/1", StringComparison.Ordinal)
                && body.Contains("calendar-data", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChangesAsyncRejectsResponseWithoutNextSyncToken()
    {
        _endpoints.Resolve("work").Returns(CalendarUri);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(207, "<d:multistatus xmlns:d=\"DAV:\" />"));

        var exception = await Should.ThrowAsync<FormatException>(
            () => Sut().GetChangesAsync("work", null, null, CancellationToken.None));

        exception.Message.ShouldContain("sync token");
    }

    [Fact]
    public async Task GetChangesAsyncSurfacesCalDavHttpFailure()
    {
        _endpoints.Resolve("work").Returns(CalendarUri);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(403, "Forbidden"));

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => Sut().GetChangesAsync("work", null, null, CancellationToken.None));

        exception.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        _parser.DidNotReceiveWithAnyArgs().Parse(default!, default!, default!, default!);
    }

    [Fact]
    public async Task GetChangesAsyncIdentifiesRejectedIncrementalSyncToken()
    {
        _endpoints.Resolve("work").Returns(CalendarUri);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CalDavResponse(
                403,
                "<d:error xmlns:d=\"DAV:\"><d:valid-sync-token /></d:error>"));

        await Should.ThrowAsync<SyncTokenRejectedException>(() =>
            Sut().GetChangesAsync("work", "expired-token", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetChangesAsyncIgnoresCalendarCollectionAndMergesSuccessfulPropertyBlocks()
    {
        const string payload = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:meeting\nEND:VEVENT\nEND:VCALENDAR";
        var parsedEvent = Event("/work/meeting.ics", "\"etag-2\"");
        _endpoints.Resolve("work").Returns(CalendarUri);
        _parser.Parse("work", "/work/meeting.ics", "\"etag-2\"", payload).Returns([parsedEvent]);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new CalDavResponse(207, $$"""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response>
                    <d:href>/work/</d:href>
                    <d:propstat><d:prop><d:getetag>collection-tag</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>/work/meeting.ics</d:href>
                    <d:propstat><d:prop><d:getetag>"etag-2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                    <d:propstat><d:prop><c:calendar-data><![CDATA[{{payload}}]]></c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                  </d:response>
                  <d:sync-token>https://icloud/token/2</d:sync-token>
                </d:multistatus>
                """));

        var page = await Sut().GetChangesAsync("work", null, null, CancellationToken.None);

        page.Changes.Count.ShouldBe(1);
        page.Changes[0].RemoteId.ShouldBe("/work/meeting.ics");
        page.Changes[0].Events.ShouldBe([parsedEvent]);
    }

    [Fact]
    public async Task GetChangesAsyncMapsPropertyLevelNotFoundToDeletion()
    {
        _endpoints.Resolve("work").Returns(CalendarUri);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new CalDavResponse(207, """
                <d:multistatus xmlns:d="DAV:">
                  <d:response>
                    <d:href>/work/deleted.ics</d:href>
                    <d:propstat><d:prop><d:getetag /></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
                  </d:response>
                  <d:sync-token>https://icloud/token/2</d:sync-token>
                </d:multistatus>
                """));

        var page = await Sut().GetChangesAsync("work", null, null, CancellationToken.None);

        page.Changes.ShouldBe([new CalendarChange("/work/deleted.ics", null)]);
    }

    [Fact]
    public async Task GetChangesAsyncFetchesAppleEventBodiesWithCalendarMultiGet()
    {
        const string payload = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:meeting\nEND:VEVENT\nEND:VCALENDAR";
        var parsedEvent = Event("/work/meeting.ics", "\"etag-2\"");
        _endpoints.Resolve("work").Returns(CalendarUri);
        _parser.Parse("work", "/work/meeting.ics", "\"etag-2\"", payload).Returns([parsedEvent]);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            call => call.ArgAt<string>(1).Contains("sync-collection", StringComparison.Ordinal)
                ? new CalDavResponse(207, """
                    <d:multistatus xmlns:d="DAV:">
                      <d:response>
                        <d:href>/work/meeting.ics</d:href>
                        <d:propstat><d:prop><d:getetag>"etag-2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                      </d:response>
                      <d:sync-token>https://icloud/token/2</d:sync-token>
                    </d:multistatus>
                    """)
                : new CalDavResponse(207, $$"""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response>
                        <d:href>/work/meeting.ics</d:href>
                        <d:propstat><d:prop><d:getetag>"etag-2"</d:getetag><c:calendar-data><![CDATA[{{payload}}]]></c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                      </d:response>
                    </d:multistatus>
                    """));

        var page = await Sut().GetChangesAsync("work", null, null, CancellationToken.None);

        page.Changes.Count.ShouldBe(1);
        page.Changes[0].Events.ShouldBe([parsedEvent]);
        await _transport.Received(1).ReportAsync(
            CalendarUri,
            Arg.Is<string>(body => body.Contains("calendar-multiget", StringComparison.Ordinal)
                && body.Contains("/work/meeting.ics", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitialSyncRequestOmitsSyncTokenElement()
    {
        _endpoints.Resolve("work").Returns(CalendarUri);
        _transport.ReportAsync(CalendarUri, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new CalDavResponse(207, """
                <d:multistatus xmlns:d="DAV:">
                  <d:sync-token>https://icloud/token/1</d:sync-token>
                </d:multistatus>
                """));

        await Sut().GetChangesAsync("work", null, null, CancellationToken.None);

        await _transport.Received(1).ReportAsync(
            CalendarUri,
            Arg.Is<string>(body => !body.Contains("sync-token", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private CalDavCalendarChangeSource Sut() => new(_transport, _endpoints, _parser);

    private static CalendarEvent Event(string id, string etag) => new(
        "work", id, etag, "Meeting",
        new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 20, 11, 0, 0, TimeSpan.Zero));

    private static string ResponseXml(string payload) => $$"""
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/work/meeting.ics</d:href>
            <d:propstat><d:prop><d:getetag>"etag-2"</d:getetag><c:calendar-data><![CDATA[{{payload}}]]></c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response><d:href>/work/deleted.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
          <d:sync-token>https://icloud/token/2</d:sync-token>
        </d:multistatus>
        """;
}
