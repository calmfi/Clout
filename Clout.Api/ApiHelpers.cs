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
            return $"0 {expr}"; // prepend seconds for Quartz compatibility
        }
        return expr;
    }
}
