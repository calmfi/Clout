using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Clout.Host.IntegrationTests;

public class IntegrationTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        System.ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["DisableQuartz"] = "true",
            };
            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            // Remove Quartz hosted service to avoid background scheduler in tests
            for (int i = services.Count - 1; i >= 0; i--)
            {
                var sd = services[i];
                if (sd.ServiceType == typeof(IHostedService) && sd.ImplementationType?.Name == "QuartzHostedService")
                {
                    services.RemoveAt(i);
                }
            }
        });
    }
}
