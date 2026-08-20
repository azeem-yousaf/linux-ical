using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class PlasmaWidgetPackageTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void MetadataTargetsPlasmaSixAndMatchesInstallationIdentifier()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "packaging", "plasma-widget", "metadata.json")));
        var root = document.RootElement;

        root.GetProperty("X-Plasma-API-Minimum-Version").GetString().ShouldBe("6.0");
        root.GetProperty("KPackageStructure").GetString().ShouldBe("Plasma/Applet");
        root.GetProperty("KPlugin").GetProperty("Id").GetString()
            .ShouldBe("com.github.azeem-yousaf.linux-ical");
    }

    [Fact]
    public void ConfigurationIsValidXmlWithSafeLocalEndpoint()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot, "packaging", "plasma-widget", "contents", "config", "main.xml"));
        XNamespace kcfg = "http://www.kde.org/standards/kcfg/1.0";
        var endpoint = document.Descendants(kcfg + "entry")
            .Single(item => item.Attribute("name")?.Value == "endpoint")
            .Element(kcfg + "default")?.Value;

        endpoint.ShouldBe("http://127.0.0.1:5088/api/widget/agenda");
    }

    [Fact]
    public void MainQmlUsesRequiredPlasmaSixRootAndUnversionedImports()
    {
        var qml = File.ReadAllText(Path.Combine(
            RepositoryRoot, "packaging", "plasma-widget", "contents", "ui", "main.qml"));

        qml.ShouldContain("PlasmoidItem {");
        qml.ShouldContain("import org.kde.plasma.plasmoid");
        qml.ShouldNotContain("import org.kde.plasma.plasmoid 2.");
        qml.ShouldContain("XMLHttpRequest");
    }

    [Fact]
    public void UserServiceBindsOnlyToLoopback()
    {
        var service = File.ReadAllText(Path.Combine(
            RepositoryRoot, "packaging", "linux-icloud-calendar.service"));

        service.ShouldContain("http://127.0.0.1:5088");
        service.ShouldNotContain("http://0.0.0.0");
        service.ShouldContain("WorkingDirectory=%h/.local/lib/linux-icloud-calendar");
    }

    [Fact]
    public void DesktopLauncherOpensTheLoopbackUiAndInstallerDeploysIt()
    {
        var desktopEntry = File.ReadAllText(Path.Combine(
            RepositoryRoot, "packaging", "linux-icloud-calendar.desktop"));
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot, "packaging", "install.sh"));
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot, ".github", "workflows", "ci-release.yml"));

        desktopEntry.ShouldContain("Exec=xdg-open http://127.0.0.1:5088/");
        desktopEntry.ShouldNotContain("0.0.0.0");
        installer.ShouldContain("linux-icloud-calendar.desktop");
        workflow.ShouldContain("packaging/linux-icloud-calendar.desktop");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ICloudCalendar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
