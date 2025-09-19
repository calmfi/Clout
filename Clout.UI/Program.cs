using Clout.UI.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Clout.UI
{
    internal sealed class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Optional Aspire service defaults (keep if you still use Aspire sometimes)
            _ = builder.AddServiceDefaults();

            // Resolve API base (supports env var, config, Aspire service discovery, fallback)
            var apiBase = ResolveApiBase(builder.Configuration, builder.Environment);

            // Razor + Fluent UI
            _ = builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            _ = builder.Services.AddFluentUIComponents();

            // Typed HttpClient for the API
            _ = builder.Services.AddHttpClient<Clout.Shared.ApiClient>(client =>
            {
                client.BaseAddress = new Uri(apiBase.clientBase);
            });

            // AppConfig used for generating browser links (download anchors etc.)
            _ = builder.Services.AddSingleton(new AppConfig(apiBase.linkBase));

            var app = builder.Build();

            app.MapDefaultEndpoints();

            if (!app.Environment.IsDevelopment())
            {
                _ = app.UseExceptionHandler("/Error");
                _ = app.UseHsts();
            }

            _ = app.UseHttpsRedirection();
            _ = app.UseAntiforgery();

            _ = app.MapStaticAssets();
            _ = app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }

        private static (string clientBase, string linkBase) ResolveApiBase(IConfiguration config, IWebHostEnvironment env)
        {
            // 1. Environment variable override (highest precedence)
            //    Useful for container / docker-compose: CLOUT_API_BASE=http://clout-host:8080
            var envVar = Environment.GetEnvironmentVariable("CLOUT_API_BASE");
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                return (Normalize(envVar!), Normalize(envVar!));
            }

            // 2. appsettings / user-secrets / command-line (--Api:BaseAddress=...)
            var cfg = config["Api:BaseAddress"];
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                return (Normalize(cfg!), Normalize(cfg!));
            }

            // 3. Aspire service discovery (if running inside Aspire orchestrator)
            //    Looks for generated service endpoint variables like:
            //    services__clout-host__https__0 or services__clout-host__http__0
            string? https = null;
            string? http = null;
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string key || entry.Value is not string val) continue;
                if (key.StartsWith("services__clout-host__https__", StringComparison.OrdinalIgnoreCase))
                    https ??= val;
                else if (key.StartsWith("services__clout-host__http__", StringComparison.OrdinalIgnoreCase))
                    http ??= val;
            }
            var discovered = https ?? http;
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                return (Normalize(discovered!), Normalize(discovered!));
            }

            // 4. Development fallback (match your dev host port; keep in sync with Host launchSettings)
            if (env.IsDevelopment())
            {
                const string dev = "http://localhost:5050";
                return (dev, dev);
            }

            // 5. Production fallback: logical DNS (e.g., when using service mesh / container DNS)
            const string logical = "http://clout-host";
            return (logical, logical);

            static string Normalize(string baseUrl)
            {
                return baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            }
        }
    }
}

