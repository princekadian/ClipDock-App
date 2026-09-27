namespace ClipDock.Infrastructure;

internal static class ErrorMessageMapper
{
    public static string ForYtDlp(string details)
    {
        if (details.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("login", StringComparison.OrdinalIgnoreCase))
            return "YouTube requires authentication for this video. ClipDock does not bypass access controls.";
        if (details.Contains("Private video", StringComparison.OrdinalIgnoreCase))
            return "This video is private and cannot be accessed.";
        if (details.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
            return "This video is unavailable or cannot be accessed from your region.";
        if (details.Contains("copyright", StringComparison.OrdinalIgnoreCase))
            return "This video is unavailable because of a copyright restriction.";
        if (details.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase))
            return "YouTube temporarily limited requests. Wait a few minutes and try again.";
        if (details.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "This YouTube link is not supported.";
        return "Unable to retrieve this video. Check that it is public and try again.";
    }
}
