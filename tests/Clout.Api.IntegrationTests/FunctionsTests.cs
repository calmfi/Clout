using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.IO;
using System;
using Cloud.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Api.IntegrationTests;

[Collection("Integration.Blobs")] // reuse same collection to serialize against shared storage folder
public class FunctionsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public FunctionsTests(WebApplicationFactory<Program> factory, Xunit.Abstractions.ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private static void CleanupStorage()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "storage");
        if (Directory.Exists(storage))
        {
            try { Directory.Delete(storage, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static string GetSampleDllPath()
    {
        // The FunctionSamples project is referenced; its DLL is copied to the test output directory
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "FunctionSamples.dll");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("FunctionSamples.dll not found in test output.", candidate);
        }
        return candidate;
    }

    [Fact]
    public async Task RegisterFunction_ValidDll_MethodFound_ReturnsCreatedWithMetadata()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        var path = GetSampleDllPath();
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent("Echo"), "name");
        form.Add(new StringContent("dotnet"), "runtime");

        var resp = await client.PostAsync("/api/functions/register", form);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(info);
        Assert.Equal(Path.GetFileName(path), info!.FileName);
        Assert.Contains(info.Metadata, m => m.Name == "function.name" && m.Value == "Echo");
        Assert.Contains(info.Metadata, m => m.Name == "function.runtime" && m.Value.Contains(".net", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(info.Metadata, m => m.Name == "function.entrypoint" && m.Value == Path.GetFileName(path));
        Assert.Contains(info.Metadata, m => m.Name == "function.verified" && m.Value == "true");
    }

    [Fact]
    public async Task RegisterFunction_MethodMissing_ReturnsBadRequest()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        var path = GetSampleDllPath();
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent("NoSuchMethodName"), "name");
        form.Add(new StringContent("dotnetcore"), "runtime");

        var resp = await client.PostAsync("/api/functions/register", form);
        if (resp.StatusCode != HttpStatusCode.BadRequest)
        {
            _output.WriteLine($"Unexpected status: {resp.StatusCode}");
            _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("could not find a public method", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schedule_SetAndClear_Works()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        // Register function
        var path = GetSampleDllPath();
        using (var form = new MultipartFormDataContent())
        {
            using var stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");

            var resp = await client.PostAsync("/api/functions/register", form);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(info);

            // Schedule with a valid cron
            var body = new { expression = "* * * * *" };
            var sch = await client.PostAsJsonAsync($"/api/functions/{info!.Id}/schedule", body);
            if (!sch.IsSuccessStatusCode)
            {
                _output.WriteLine("Schedule body: " + await sch.Content.ReadAsStringAsync());
            }
            Assert.True(sch.IsSuccessStatusCode);
            var after = await sch.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(after);
            Assert.Contains(after!.Metadata, m => m.Name == "TimerTrigger" && m.Value == "* * * * *");

            // Clear schedule
            var del = await client.DeleteAsync($"/api/functions/{info.Id}/schedule");
            Assert.True(del.IsSuccessStatusCode);
            var cleared = await del.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(cleared);
            Assert.DoesNotContain(cleared!.Metadata, m => m.Name == "TimerTrigger");
        }
    }

    [Fact]
    public async Task Schedule_InvalidCron_ReturnsBadRequest()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        // Register function
        var path = GetSampleDllPath();
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent("Echo"), "name");
        form.Add(new StringContent("dotnet"), "runtime");
        var resp = await client.PostAsync("/api/functions/register", form);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(info);

        // Attempt schedule with invalid expression
        var body = new { expression = "invalid cron" };
        var sch = await client.PostAsJsonAsync($"/api/functions/{info!.Id}/schedule", body);
        Assert.Equal(HttpStatusCode.BadRequest, sch.StatusCode);
        var err = await sch.Content.ReadAsStringAsync();
        Assert.Contains("Invalid NCRONTAB", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterScheduled_Valid_SetsFunctionAndTimerMetadata()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        var path = GetSampleDllPath();
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent("Echo"), "name");
        form.Add(new StringContent("dotnet"), "runtime");
        form.Add(new StringContent("* * * * *"), "cron");

        var resp = await client.PostAsync("/api/functions/register/scheduled", form);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(info);
        Assert.Contains(info!.Metadata, m => m.Name == "function.name" && m.Value == "Echo");
        Assert.Contains(info.Metadata, m => m.Name == "TimerTrigger" && m.Value == "* * * * *");
    }

    [Fact]
    public async Task RegisterScheduled_InvalidCron_ReturnsBadRequest()
    {
        CleanupStorage();
        var client = _factory.CreateClient();

        var path = GetSampleDllPath();
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent("Echo"), "name");
        form.Add(new StringContent("dotnet"), "runtime");
        form.Add(new StringContent("not a cron"), "cron");

        var resp = await client.PostAsync("/api/functions/register/scheduled", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Invalid NCRONTAB", body, StringComparison.OrdinalIgnoreCase);
    }
}
