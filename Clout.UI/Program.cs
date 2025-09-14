using Microsoft.FluentUI.AspNetCore.Components;
using Clout.UI.Components;
using Clout.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

// Register Fluent UI components
builder.Services.AddFluentUIComponents();

// API client & base URL
var baseUrl = Environment.GetEnvironmentVariable("CLOUT_API");
if (string.IsNullOrWhiteSpace(baseUrl))
{
    baseUrl = "http://localhost:5000";
}

builder.Services.AddAntiforgery();
builder.Services.AddSingleton(new ApiConfig { BaseUrl = baseUrl! });
builder.Services.AddHttpClient<ICloutApiClient, CloutApiClient>(client =>
{
    client.BaseAddress = new Uri(baseUrl!);
});

// Toasts
builder.Services.AddSingleton<Clout.UI.Services.ToastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseAntiforgery();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
