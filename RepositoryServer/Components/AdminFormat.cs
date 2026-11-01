namespace OpenShock.RepositoryServer.Components;

/// <summary>How the admin pages print values, so the same thing reads the same on every page.</summary>
public static class AdminFormat
{
    /// <summary>An enum's name as display text. They are single capitalised words throughout.</summary>
    public static string Label<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString();

    /// <summary>"1 board", "3 boards". Only for regular plurals, which is all this UI needs.</summary>
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public static string ShortCommit(string commit) => commit.Length <= 7 ? commit : commit[..7];

    /// <summary>"3 hours ago", falling back to a date once the distance stops meaning much.</summary>
    public static string Relative(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var elapsed = (now ?? DateTimeOffset.UtcNow) - value;

        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{Count((int)elapsed.TotalMinutes, "minute")} ago";
        if (elapsed.TotalDays < 1) return $"{Count((int)elapsed.TotalHours, "hour")} ago";
        if (elapsed.TotalDays < 60) return $"{Count((int)elapsed.TotalDays, "day")} ago";

        return value.ToUniversalTime().ToString("MMM d, yyyy");
    }
}
