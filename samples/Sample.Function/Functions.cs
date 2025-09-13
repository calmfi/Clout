namespace Sample.Function;

public static class Functions
{
    // Public parameterless method that can be invoked by the timer service and registration validator
    public static void Ping()
    {
        Console.WriteLine($"[Sample.Function] Ping at {DateTime.UtcNow:u} UTC");
    }
}

