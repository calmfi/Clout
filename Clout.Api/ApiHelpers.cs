internal static class ApiHelpers
{
    public static bool TryParseSchedule(string expr, out NCrontab.CrontabSchedule schedule)
    {
        try
        {
            schedule = NCrontab.CrontabSchedule.Parse(expr, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = true });
            return true;
        }
        catch { }
        try
        {
            schedule = NCrontab.CrontabSchedule.Parse(expr, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = false });
            return true;
        }
        catch
        {
            schedule = null!;
            return false;
        }
    }
}