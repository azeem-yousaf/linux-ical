namespace ICloudCalendar.Core;

public sealed class CalendarSyncService(
    ICalendarChangeSource source,
    ICalendarStore store,
    IClock clock)
{
    public async Task<SyncResult> SyncAsync(string calendarId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);

        var checkpoint = await store.GetCheckpointAsync(calendarId, cancellationToken);
        var upserts = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
        var deletions = new HashSet<string>(StringComparer.Ordinal);
        var requestedSyncToken = checkpoint.SyncToken;
        string? nextSyncToken;
        while (true)
        {
            upserts.Clear();
            deletions.Clear();
            string? pageCursor = null;
            nextSyncToken = requestedSyncToken;
            try
            {
                do
                {
                    var page = await source.GetChangesAsync(
                        calendarId,
                        requestedSyncToken,
                        pageCursor,
                        cancellationToken);

                    foreach (var change in page.Changes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ArgumentException.ThrowIfNullOrWhiteSpace(change.RemoteId);

                        if (change.IsDeletion)
                        {
                            foreach (var eventId in upserts
                                .Where(item => StringComparer.Ordinal.Equals(item.Value.SourceRemoteId ?? item.Value.RemoteId, change.RemoteId))
                                .Select(item => item.Key)
                                .ToArray())
                            {
                                upserts.Remove(eventId);
                            }
                            deletions.Add(change.RemoteId);
                            continue;
                        }

                        deletions.Remove(change.RemoteId);
                        foreach (var eventId in upserts
                            .Where(item => StringComparer.Ordinal.Equals(item.Value.SourceRemoteId ?? item.Value.RemoteId, change.RemoteId))
                            .Select(item => item.Key)
                            .ToArray())
                        {
                            upserts.Remove(eventId);
                        }
                        if (change.Events!.Count == 0)
                        {
                            // A recurring resource can legitimately project no events after
                            // Apple truncates it while splitting an edited series. Treat that
                            // as replacement with an empty projection, clearing cached rows.
                            deletions.Add(change.RemoteId);
                            continue;
                        }
                        foreach (var calendarEvent in change.Events!)
                        {
                            calendarEvent.Validate();
                            if (!StringComparer.Ordinal.Equals(calendarId, calendarEvent.CalendarId))
                            {
                                throw new InvalidOperationException("The change source returned an event for a different calendar.");
                            }

                            upserts[calendarEvent.RemoteId] = calendarEvent;
                        }
                    }

                    pageCursor = page.NextPageCursor;
                    nextSyncToken = page.NextSyncToken ?? nextSyncToken;
                }
                while (pageCursor is not null);
                break;
            }
            catch (SyncTokenRejectedException) when (requestedSyncToken is not null)
            {
                // A server can expire a sync token at any time. Retrying without it
                // obtains a complete snapshot, which replaces local state atomically.
                requestedSyncToken = null;
            }
        }

        var completedAt = clock.UtcNow;
        await store.ApplyAsync(
            calendarId,
            upserts.Values.ToArray(),
            deletions.ToArray(),
            requestedSyncToken is null,
            nextSyncToken,
            completedAt,
            cancellationToken);

        return new SyncResult(upserts.Count, deletions.Count, nextSyncToken, completedAt);
    }
}
