namespace OpenShock.RepositoryServer.Components.Layout;

public sealed record AdminNavItem(string Title, IconName Icon, string Href);

public sealed record AdminNavGroup(string Title, AdminNavItem[] Items);

public sealed record AdminCrumb(string Label, string? Href);

/// <summary>
/// The admin navigation, as data: the sidebar and the breadcrumb both render from this, so a page
/// added here cannot be missing from either (style guide §7.5).
/// </summary>
/// <remarks>
/// Grouped by the job rather than by which table a page edits. Releases and advisories are what an
/// admin acts on when something ships; boards, chips and USB ids are reference data that rarely
/// changes; and who may publish, who gets notified and bulk import are instance setup, so they sit
/// at the bottom of the sidebar the way the Frontend pins Admin and Settings. Routes stay as they
/// were: they are documented and bookmarked, and do not have to mirror the grouping.
/// </remarks>
public static class AdminNav
{
    public const string OverviewHref = "/admin";

    public static readonly AdminNavItem Overview = new("Overview", IconName.Dashboard, OverviewHref);

    public static readonly AdminNavGroup[] Primary =
    [
        new("Firmware",
        [
            new("Releases", IconName.Rocket, "/admin/firmware/releases"),
            new("Advisories", IconName.Shield, "/admin/firmware/advisories")
        ]),
        new("Hardware",
        [
            new("Boards", IconName.Board, "/admin/firmware/boards"),
            new("Chips", IconName.Chip, "/admin/firmware/chips"),
            new("USB devices", IconName.Usb, "/admin/firmware/usb-devices"),
            new("Serial filters", IconName.Filter, "/admin/firmware/usb-serial-filters")
        ]),
        new("Desktop",
        [
            new("Modules", IconName.Puzzle, "/admin/desktop/modules")
        ])
    ];

    public static readonly AdminNavGroup[] Footer =
    [
        new("Administration",
        [
            new("Publishers", IconName.KeyRound, "/admin/repositories"),
            new("Discord webhooks", IconName.Bell, "/admin/discord-webhooks"),
            new("Import", IconName.Upload, "/admin/import")
        ])
    ];

    /// <summary>
    /// The breadcrumb for a path: its group, its page, then whatever record the rest of the path names.
    /// Empty for a path outside the navigation, such as the not-found page.
    /// </summary>
    public static IReadOnlyList<AdminCrumb> Trail(string path)
    {
        path = "/" + path.Split('?', '#')[0].Trim('/');

        if (path == OverviewHref) return [new AdminCrumb(Overview.Title, null)];

        foreach (var group in Primary.Concat(Footer))
        {
            foreach (var item in group.Items)
            {
                if (path == item.Href)
                {
                    return [new AdminCrumb(group.Title, null), new AdminCrumb(item.Title, null)];
                }

                if (path.StartsWith(item.Href + "/", StringComparison.Ordinal))
                {
                    // A record's own id, which is lowercase by definition: printed as it is.
                    var record = Uri.UnescapeDataString(path[(item.Href.Length + 1)..]);
                    return
                    [
                        new AdminCrumb(group.Title, null),
                        new AdminCrumb(item.Title, item.Href),
                        new AdminCrumb(record, null)
                    ];
                }
            }
        }

        return [];
    }
}
