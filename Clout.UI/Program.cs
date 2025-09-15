using Clout.UI.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Clout.UI
{
    internal sealed class Program
    {
        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            _ = builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            _ = builder.Services.AddFluentUIComponents();

            // Register API client for the Local Cloud API
            var apiBase = Environment.GetEnvironmentVariable("CLOUT_API") ?? "http://localhost:5000";
            _ = builder.Services.AddSingleton(_ => new Clout.Shared.BlobApiClient(apiBase));
            _ = builder.Services.AddSingleton(new AppConfig(apiBase));

            WebApplication app = builder.Build();

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
    }
}
