using System.Net;
using ICloudCalendar.Infrastructure.CalDav;
using ICloudCalendar.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalDavSyncIntegrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"icloud-integration-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task FullThenIncrementalSyncFlowsFromCalDavThroughSqliteAgenda()
    {
        var handler = new ScriptedCalDavHandler([FullResponse, DeleteResponse]);
        using var client = new HttpClient(handler);
        var source = new CalDavCalendarChangeSource(
            new HttpCalDavTransport(client),
            new FixedCalendarEndpointResolver(new Uri("https://p01-caldav.icloud.com/calendars/work/")),
            new IcalNetCalendarPayloadParser(new FixedWindow()));
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        var database = new SqliteDatabaseInitializer(connections);
        var store = new SqliteCalendarStore(connections, database);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));
        var sync = new CalendarSyncService(source, store, clock);

        var initial = await sync.SyncAsync("work");
        var firstAgenda = await store.GetAgendaAsync(
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));

        initial.ShouldBe(new SyncResult(1, 0, "token-1", clock.UtcNow));
        firstAgenda.Single().Title.ShouldBe("Architecture review");
        handler.RequestBodies[0].ShouldNotContain("sync-token");

        var incremental = await sync.SyncAsync("work");
        var secondAgenda = await store.GetAgendaAsync(
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));

        incremental.Deleted.ShouldBe(1);
        incremental.SyncToken.ShouldBe("token-2");
        secondAgenda.ShouldBeEmpty();
        handler.RequestBodies[1].ShouldContain("<d:sync-token>token-1</d:sync-token>");
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class FixedWindow : ICalendarProjectionWindow
    {
        public DateTimeOffset StartsAt => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset EndsAt => new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class ScriptedCalDavHandler(IReadOnlyList<string> responses) : HttpMessageHandler
    {
        private int _requestIndex;
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Method.Method.ShouldBe("REPORT");
            request.Headers.GetValues("Depth").Single().ShouldBe("1");
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                RequestMessage = request,
                Content = new StringContent(responses[_requestIndex++])
            };
        }
    }

    private const string FullResponse = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/calendars/work/review.ics</d:href><d:propstat><d:prop>
            <d:getetag>"etag-1"</d:getetag><c:calendar-data><![CDATA[BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Linux iCloud Calendar//EN
        BEGIN:VEVENT
        UID:review
        DTSTAMP:20260820T070000Z
        DTSTART:20260820T090000Z
        DTEND:20260820T100000Z
        SUMMARY:Architecture review
        END:VEVENT
        END:VCALENDAR]]></c:calendar-data>
          </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:sync-token>token-1</d:sync-token>
        </d:multistatus>
        """;

    private const string DeleteResponse = """
        <d:multistatus xmlns:d="DAV:">
          <d:response><d:href>/calendars/work/review.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
          <d:sync-token>token-2</d:sync-token>
        </d:multistatus>
        """;
}
