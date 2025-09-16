public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var cloutHost = builder
            .AddProject<Projects.Clout_Host>("clout-host");

        builder.AddProject<Projects.Clout_UI>("clout-ui")
            .WithReference(cloutHost);

        await builder.Build().RunAsync();
    }
}