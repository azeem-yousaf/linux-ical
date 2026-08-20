using System.Net.Http.Headers;
using System.Text;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class HttpCalDavTransport(HttpClient httpClient) : ICalDavTransport
{
    private static readonly HttpMethod ReportMethod = new("REPORT");
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");

    public async Task<CalDavResponse> ReportAsync(
        Uri calendarUri,
        string requestBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendarUri);
        ArgumentNullException.ThrowIfNull(requestBody);

        return await SendAsync(ReportMethod, calendarUri, requestBody, 1, cancellationToken);
    }

    public Task<CalDavResponse> PropFindAsync(
        Uri resourceUri,
        string requestBody,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        return SendAsync(PropFindMethod, resourceUri, requestBody, depth, cancellationToken);
    }

    private async Task<CalDavResponse> SendAsync(
        HttpMethod method,
        Uri resourceUri,
        string requestBody,
        int depth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, resourceUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        return new CalDavResponse((int)response.StatusCode, content, response.RequestMessage?.RequestUri);
    }
}
