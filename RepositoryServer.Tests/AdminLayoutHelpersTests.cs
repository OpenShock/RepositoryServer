using OpenShock.RepositoryServer.Components;
using OpenShock.RepositoryServer.Components.Layout;

namespace OpenShock.RepositoryServer.Tests;

public class AdminLayoutHelpersTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(0, "just now")]
    [Arguments(1, "1 minute ago")]
    [Arguments(90, "1 hour ago")]
    [Arguments(60 * 24 * 3, "3 days ago")]
    public async Task Relative_NearbyTimes_ReadAsDistance(int minutesAgo, string expected)
    {
        await Assert.That(AdminFormat.Relative(Now.AddMinutes(-minutesAgo), Now)).IsEqualTo(expected);
    }

    [Test]
    public async Task Relative_DistantTime_ReadsAsDate()
    {
        await Assert.That(AdminFormat.Relative(Now.AddDays(-120), Now)).IsEqualTo("Feb 15, 2026");
    }

    [Test]
    public async Task Trail_Overview_IsOneCrumb()
    {
        var trail = AdminNav.Trail("admin");

        await Assert.That(trail.Count).IsEqualTo(1);
        await Assert.That(trail[0].Label).IsEqualTo("Overview");
    }

    [Test]
    public async Task Trail_Page_NamesItsGroup()
    {
        var trail = AdminNav.Trail("admin/firmware/boards?page=2");

        await Assert.That(trail.Select(c => c.Label)).IsEquivalentTo(["Hardware", "Boards"]);
    }

    [Test]
    public async Task Trail_Record_LinksBackToItsPage_AndKeepsTheIdAsWritten()
    {
        var trail = AdminNav.Trail("admin/desktop/modules/openshock.overlay");

        await Assert.That(trail.Count).IsEqualTo(3);
        await Assert.That(trail[1].Href).IsEqualTo("/admin/desktop/modules");
        await Assert.That(trail[2].Label).IsEqualTo("openshock.overlay");
    }

    [Test]
    public async Task Trail_UnknownPath_IsEmpty()
    {
        await Assert.That(AdminNav.Trail("admin/nonsense").Count).IsEqualTo(0);
    }

    // A prefix match without the separator would file this under Boards.
    [Test]
    public async Task Trail_PathSharingAPrefix_DoesNotMatch()
    {
        await Assert.That(AdminNav.Trail("admin/firmware/boardsextra").Count).IsEqualTo(0);
    }
}
