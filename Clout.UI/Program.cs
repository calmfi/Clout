using Clout.UI.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Clout.UI
{
    internal sealed class Program
    {
        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            
            _ = builder.AddServiceDefaults();

            // Add services to the container.
            _ = builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            _ = builder.Services.AddFluentUIComponents();

            // Register API client using Aspire service discovery
            var apiBase = ResolveApiBase();
            _ = builder.Services.AddHttpClient<Clout.Shared.BlobApiClient>(client =>
            {
                client.BaseAddress = new Uri(apiBase.clientBase);
            });
            _ = builder.Services.AddSingleton(new AppConfig(apiBase.linkBase));

            WebApplication app = builder.Build();

            app.MapDefaultEndpoints();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                _ = app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                _ = app.UseHsts();
            }

            _ = app.UseHttpsRedirection();

            _ = app.UseAntiforgery();

            _ = app.MapStaticAssets();
            _ = app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }

        private static (string clientBase, string linkBase) ResolveApiBase()
        {
            // Try to discover Aspire-provided endpoint env vars for the referenced service
            // Common pattern: services__<name>__http__0 or services__<name>__https__0
            // Prefer https if present
            var env = Environment.GetEnvironmentVariables();
            string? https = null;
            string? http = null;
            foreach (System.Collections.DictionaryEntry entry in env)
            {
                var key = entry.Key as string;
                if (string.IsNullOrEmpty(key)) continue;
                var val = entry.Value as string;
                if (string.IsNullOrEmpty(val)) continue;
                if (key.StartsWith("services__clout-host__https__", StringComparison.OrdinalIgnoreCase))
                {
                    https ??= val;
                }
                else if (key.StartsWith("services__clout-host__http__", StringComparison.OrdinalIgnoreCase))
                {
                    http ??= val;
                }
            }

            var discovered = https ?? http;
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                // Use discovered absolute for both client + browser links
                return (discovered!, discovered!);
            }

            // Fallback: let HttpClient resolve via service discovery; links use same base
            const string serviceNameBase = "http://clout-host";
            return (serviceNameBase, serviceNameBase);
        }
    }
}
