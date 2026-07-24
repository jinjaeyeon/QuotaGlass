namespace QuotaGlass.Models;

public sealed record UsageMeter(
    string Id,
    string Label,
    double Remaining,
    double Limit,
    string Unit,
    DateTimeOffset WindowStart,
    DateTimeOffset ResetsAt,
    bool IsReset = false)
{
    public double RemainingRatio =>
        Limit <= 0 ? 0 : Math.Clamp(Remaining / Limit, 0, 1);

    public double RemainingTimeRatio(DateTimeOffset now)
    {
        var total = ResetsAt - WindowStart;
        if (total <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Clamp((ResetsAt - now).TotalSeconds / total.TotalSeconds, 0, 1);
    }

    public double PaceDelta(DateTimeOffset now) =>
        RemainingRatio - RemainingTimeRatio(now);
}
