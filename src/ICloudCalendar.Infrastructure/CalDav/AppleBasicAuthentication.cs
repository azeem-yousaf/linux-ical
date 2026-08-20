using System.Net.Http.Headers;
using System.Text;

namespace ICloudCalendar.Infrastructure.CalDav;

internal static class AppleBasicAuthentication
{
    public static AuthenticationHeaderValue Create(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
    }
}
