using System.Net;
using System.Security.Cryptography;
using System.Text;
using ICloudCalendar.Infrastructure.Security;
using ICloudCalendar.Core;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed record AppleAccountProfile(
    string AccountId,
    string UserName,
    IReadOnlyList<DiscoveredCalendar> Calendars,
    IReadOnlyList<CalendarSyncOutcome> InitialSync);

public interface IAppleCalendarProbe
{
    Task<IReadOnlyList<DiscoveredCalendar>> DiscoverAsync(
        string userName,
        string appSpecificPassword,
        CancellationToken cancellationToken);
}

public interface IAppleCalendarOnboarding
{
    Task<AppleAccountProfile> ConnectAsync(
        string userName,
        string appSpecificPassword,
        CancellationToken cancellationToken = default);
}

public interface IAppleAccountManager
{
    Task DisconnectAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed class AppleAccountManager(
    ICredentialVault credentialVault,
    IAccountCatalog accounts) : IAppleAccountManager
{
    public async Task DisconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        // Prefer removing the secret first. If local cleanup fails, the account is
        // inert and can safely be removed or reconnected on the next attempt.
        await credentialVault.DeleteAsync(accountId, cancellationToken);
        await accounts.DeleteAsync(accountId, cancellationToken);
    }
}

public sealed class AppleCalendarOnboarding(
    IAppleCalendarProbe probe,
    ICredentialVault credentialVault,
    IAccountCatalog accounts,
    IAccountSynchronizer synchronizer,
    IAccountDiscoveryRefresher discoveryRefresher) : IAppleCalendarOnboarding
{
    public async Task<AppleAccountProfile> ConnectAsync(
        string userName,
        string appSpecificPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSpecificPassword);
        var normalizedUserName = userName.Trim();
        var normalizedPassword = appSpecificPassword.Trim();

        // Authentication and discovery must succeed before any credential is persisted.
        var calendars = await probe.DiscoverAsync(normalizedUserName, normalizedPassword, cancellationToken);
        var accountId = StableAccountId(normalizedUserName);
        var previousCredential = await credentialVault.RetrieveAsync(accountId, cancellationToken);
        await credentialVault.StoreAsync(accountId, normalizedPassword, cancellationToken);
        try
        {
            await accounts.SaveAsync(
                new CalendarAccount(accountId, normalizedUserName),
                calendars.Select(item => new CalendarSubscription(
                    item.Id,
                    accountId,
                    item.DisplayName,
                    item.Uri,
                    item.Color)).ToArray(),
                cancellationToken);
        }
        catch
        {
            if (string.IsNullOrEmpty(previousCredential))
            {
                await credentialVault.DeleteAsync(accountId, CancellationToken.None);
            }
            else
            {
                await credentialVault.StoreAsync(accountId, previousCredential, CancellationToken.None);
            }
            throw;
        }

        discoveryRefresher.MarkCurrent(accountId);
        var initialSync = await synchronizer.SyncAsync(accountId, cancellationToken);

        return new AppleAccountProfile(accountId, normalizedUserName, calendars, initialSync);
    }

    private static string StableAccountId(string userName) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(userName.ToUpperInvariant()))).ToLowerInvariant()[..20];
}

public sealed class HttpAppleCalendarProbe : IAppleCalendarProbe
{
    private static readonly Uri AppleCalDavUri = new("https://caldav.icloud.com/");

    public async Task<IReadOnlyList<DiscoveredCalendar>> DiscoverAsync(
        string userName,
        string appSpecificPassword,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new ICloudSafeRedirectHandler(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            },
            AppleBasicAuthentication.Create(userName, appSpecificPassword)))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxICloudCalendar/0.1");

        var discovery = new CalDavCalendarDiscovery(new HttpCalDavTransport(client));
        return await discovery.DiscoverAsync(AppleCalDavUri, cancellationToken);
    }

}
