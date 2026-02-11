using Quartz;

namespace Clout.Shared;

/// <summary>
/// Unified cron expression utilities for converting NCRONTAB to Quartz format.
/// </summary>
public static class CronHelper
{
    private static readonly char[] SplitWhitespace = [' ', '\t'];

    /// <summary>
    /// Converts a 5-field NCRONTAB expression (minute hour dom month dow) to a
    /// 6-field Quartz expression (second minute hour dom month ?).
    /// If the expression already has 6+ fields, it is returned unchanged.
    /// </summary>
    public static string ToQuartzCron(string expr)
    {
        var parts = expr.Split(SplitWhitespace, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5)
        {
            return $"0 {parts[0]} {parts[1]} {parts[2]} {parts[3]} ?";
        }
        return expr;
    }

    /// <summary>
    /// Attempts to parse and validate a cron expression (5- or 6-field).
    /// Normalizes 5-field NCRONTAB to Quartz format before validation.
    /// </summary>
    public static bool TryParseSchedule(string expr, out CronExpression? schedule)
    {
        var normalized = ToQuartzCron(expr);
        if (CronExpression.IsValidExpression(normalized))
        {
            schedule = new CronExpression(normalized);
            return true;
        }
        schedule = null;
        return false;
    }

    /// <summary>
    /// Returns true if the cron expression is syntactically valid (Quartz format, with or without seconds).
    /// </summary>
    public static bool IsValid(string expr) => TryParseSchedule(expr, out _);
}
