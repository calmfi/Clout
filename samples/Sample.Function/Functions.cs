namespace Sample.Function
{
    public static class Functions
    {
        // Public parameterless method that can be invoked by the timer service and registration validator
        public static void Ping()
        {
            Console.WriteLine($"[Sample.Functions] Ping at {DateTime.UtcNow:u} UTC");
        }

        public static void Pong()
        {
            Console.WriteLine($"[Sample.Functions] Pong at {DateTime.UtcNow:u} UTC");
        }
    }

}
