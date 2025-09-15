using Quartz;

internal static class ApiHelpers
{
    public static bool TryParseSchedule(string expr, out CronExpression? schedule)
    {
        var normalized = NormalizeForQuartz(expr);
        if (CronExpression.IsValidExpression(normalized))
        {
            schedule = new CronExpression(normalized);
            return true;
        }
        schedule = null;
        return false;
    }

    private static string NormalizeForQuartz(string expr)
    {
        var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5)
        {
            // Convert NCRONTAB (m h dom mon dow) to Quartz (s m h dom mon dow)
            // Use '?' for day-of-week to satisfy Quartz requirement for one of day fields
            var m = parts[0];
            var h = parts[1];
            var dom = parts[2];
            var mon = parts[3];
            // ignore NCRONTAB dow and set '?'
            return $"0 {m} {h} {dom} {mon} ?";
        }
        return expr;
    }
}
