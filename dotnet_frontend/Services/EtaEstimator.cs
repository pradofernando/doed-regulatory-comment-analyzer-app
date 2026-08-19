namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Pure helpers for estimating and formatting time-to-completion of a long-running
/// analysis run. Kept free of UI/IO concerns so it can be unit tested.
/// </summary>
public static class EtaEstimator
{
    /// <summary>
    /// Estimates the time remaining given how long the current phase has run and how many
    /// of <paramref name="total"/> items are done. Returns <c>null</c> when an estimate
    /// isn't meaningful yet (nothing done, bad totals, or already complete).
    /// </summary>
    public static TimeSpan? Estimate(TimeSpan elapsed, int completed, int total)
    {
        if (completed <= 0 || total <= 0 || completed >= total) return null;
        if (elapsed <= TimeSpan.Zero) return null;

        var perItemSeconds = elapsed.TotalSeconds / completed;
        var remainingSeconds = perItemSeconds * (total - completed);
        return TimeSpan.FromSeconds(remainingSeconds);
    }

    /// <summary>
    /// Compact, human-friendly duration: "45s", "2m 05s", or "1h 03m".
    /// Negative spans are clamped to zero.
    /// </summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes:D2}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds:D2}s";
        return $"{span.Seconds}s";
    }
}
