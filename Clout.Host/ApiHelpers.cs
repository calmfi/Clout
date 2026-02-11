using Clout.Shared;
using Quartz;

internal static class ApiHelpers
{
    public static bool TryParseSchedule(string expr, out CronExpression? schedule)
        => CronHelper.TryParseSchedule(expr, out schedule);
}
