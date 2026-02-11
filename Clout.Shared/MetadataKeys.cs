namespace Clout.Shared;

/// <summary>
/// Well-known blob metadata key names used for function registration and triggers.
/// </summary>
public static class MetadataKeys
{
    public const string FunctionName = "function.name";
    public const string FunctionRuntime = "function.runtime";
    public const string FunctionEntrypoint = "function.entrypoint";
    public const string FunctionDeclaringType = "function.declaringType";
    public const string FunctionVerified = "function.verified";
    public const string FunctionSourceId = "function.sourceId";
    public const string TimerTrigger = "TimerTrigger";
    public const string QueueTrigger = "QueueTrigger";
}
