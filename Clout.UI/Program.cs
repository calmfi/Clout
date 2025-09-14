using Clout.UI.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Clout.UI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddFluentUIComponents();

            // Register API client for the Local Cloud API
            var apiBase = Environment.GetEnvironmentVariable("CLOUT_API") ?? "http://localhost:5000";
            builder.Services.AddSingleton(new Cloud.Shared.BlobApiClient(apiBase));
            builder.Services.AddSingleton(new AppConfig(apiBase));

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
