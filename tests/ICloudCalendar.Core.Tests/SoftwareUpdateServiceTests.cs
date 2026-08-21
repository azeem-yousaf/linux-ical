using System.Text.Json;
using ICloudCalendar.Web.Services;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SoftwareUpdateServiceTests
{
    [Fact]
    public void ParseReleaseSelectsOnlyTheSignedPackageForThisArchitecture()
    {
        var runtime = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "linux-arm64" : "linux-x64";
        using var document = JsonDocument.Parse($$"""
            {"tag_name":"v1.2.0","html_url":"https://github.com/azeem-yousaf/linux-ical/releases/tag/v1.2.0","assets":[
              {"name":"linux-icloud-calendar-v1.2.0-{{runtime}}.tar.gz","browser_download_url":"https://github.com/azeem-yousaf/linux-ical/releases/download/v1.2.0/linux-icloud-calendar-v1.2.0-{{runtime}}.tar.gz"},
              {"name":"SHA256SUMS","browser_download_url":"https://github.com/azeem-yousaf/linux-ical/releases/download/v1.2.0/SHA256SUMS"}]}
            """);

        var update = SoftwareUpdateService.ParseRelease("1.1.0", document.RootElement);

        update.UpdateAvailable.ShouldBeTrue();
        update.LatestVersion.ShouldBe("1.2.0");
        update.Runtime.ShouldBe(runtime);
        update.ArchiveUrl.ShouldEndWith($"linux-icloud-calendar-v1.2.0-{runtime}.tar.gz");
        update.ChecksumsUrl.ShouldEndWith("/SHA256SUMS");
    }

    [Fact]
    public void ParseReleaseRejectsAssetsFromAnUnexpectedDownloadLocation()
    {
        var runtime = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "linux-arm64" : "linux-x64";
        using var document = JsonDocument.Parse($$"""
            {"tag_name":"v9.0.0","html_url":"https://github.com/azeem-yousaf/linux-ical/releases/tag/v9.0.0","assets":[
              {"name":"linux-icloud-calendar-v9.0.0-{{runtime}}.tar.gz","browser_download_url":"https://example.com/update.tar.gz"},
              {"name":"SHA256SUMS","browser_download_url":"https://example.com/SHA256SUMS"}]}
            """);

        SoftwareUpdateService.ParseRelease("1.1.0", document.RootElement).UpdateAvailable.ShouldBeFalse();
    }
}
