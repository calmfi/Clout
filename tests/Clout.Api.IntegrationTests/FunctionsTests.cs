using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Cloud.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Api.IntegrationTests
{
    [Collection("Integration.Blobs")] // reuse same collection to serialize against shared storage folder
    internal class FunctionsTests(WebApplicationFactory<Program> factory, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory = factory;
        private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

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
            return !File.Exists(candidate)
                ? throw new FileNotFoundException("FunctionSamples.dll not found in test output.", candidate)
                : candidate;
        }

        [Fact]
        public async Task RegisterFunctionValidDllMethodFoundReturnsCreatedWithMetadata()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");

            HttpResponseMessage resp = await client.PostAsync("/api/functions/register", form).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.Created)
            {
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);
            Assert.Equal(Path.GetFileName(path), info!.FileName);
            Assert.Contains(info.Metadata, m => m.Name == "function.name" && m.Value == "Echo");
            Assert.Contains(info.Metadata, m => m.Name == "function.runtime" && m.Value.Contains(".net", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(info.Metadata, m => m.Name == "function.entrypoint" && m.Value == Path.GetFileName(path));
            Assert.Contains(info.Metadata, m => m.Name == "function.verified" && m.Value == "true");
        }

        [Fact]
        public async Task RegisterFunctionMethodMissingReturnsBadRequest()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("NoSuchMethodName"), "name");
            form.Add(new StringContent("dotnetcore"), "runtime");

            HttpResponseMessage resp = await client.PostAsync("/api/functions/register", form).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.BadRequest)
            {
                _output.WriteLine($"Unexpected status: {resp.StatusCode}");
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("could not find a public method", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ScheduleSetAndClearWorks()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            // Register function
            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");

            HttpResponseMessage resp = await client.PostAsync("/api/functions/register", form).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);

            // Schedule with a valid cron
            var body = new { expression = "* * * * *" };
            HttpResponseMessage sch = await client.PostAsJsonAsync($"/api/functions/{info!.Id}/schedule", body).ConfigureAwait(false);
            if (!sch.IsSuccessStatusCode)
            {
                _output.WriteLine("Schedule body: " + await sch.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.True(sch.IsSuccessStatusCode);
            BlobInfo? after = await sch.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(after);
            Assert.Contains(after!.Metadata, m => m.Name == "TimerTrigger" && m.Value == "* * * * *");

            // Clear schedule
            HttpResponseMessage del = await client.DeleteAsync($"/api/functions/{info.Id}/schedule").ConfigureAwait(false);
            Assert.True(del.IsSuccessStatusCode);
            BlobInfo? cleared = await del.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(cleared);
            Assert.DoesNotContain(cleared!.Metadata, m => m.Name == "TimerTrigger");
        }

        [Fact]
        public async Task ScheduleInvalidCronReturnsBadRequest()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            // Register function
            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");
            HttpResponseMessage resp = await client.PostAsync("/api/functions/register", form).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);

            // Attempt schedule with invalid expression
            var body = new { expression = "invalid cron" };
            HttpResponseMessage sch = await client.PostAsJsonAsync($"/api/functions/{info!.Id}/schedule", body).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, sch.StatusCode);
            var err = await sch.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("Invalid NCRONTAB", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RegisterScheduledValidSetsFunctionAndTimerMetadata()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");
            form.Add(new StringContent("* * * * *"), "cron");

            HttpResponseMessage resp = await client.PostAsync("/api/functions/register/scheduled", form).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.Created)
            {
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);
            Assert.Contains(info!.Metadata, m => m.Name == "function.name" && m.Value == "Echo");
            Assert.Contains(info.Metadata, m => m.Name == "TimerTrigger" && m.Value == "* * * * *");
        }

        [Fact]
        public async Task RegisterScheduledInvalidCronReturnsBadRequest()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent("Echo"), "name");
            form.Add(new StringContent("dotnet"), "runtime");
            form.Add(new StringContent("not a cron"), "cron");

            HttpResponseMessage resp = await client.PostAsync("/api/functions/register/scheduled", form).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("Invalid NCRONTAB", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
