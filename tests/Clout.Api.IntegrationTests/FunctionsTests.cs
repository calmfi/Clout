using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Clout.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Host.IntegrationTests
{
    [Collection("Integration.Blobs")] // reuse same collection to serialize against shared storage folder
    public class FunctionsTests(IntegrationTestFactory factory, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<IntegrationTestFactory>
    {
        private readonly WebApplicationFactory<Program> _factory = factory;
        private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

        private static void CleanupStorage()
        {
            var storage = Path.Combine(AppContext.BaseDirectory, "storage");
            if (Directory.Exists(storage))
            {
                try { Directory.Delete(storage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
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
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc1 = new StringContent("Echo");
            using var sc2 = new StringContent("dotnet");
            form.Add(sc1, "name");
            form.Add(sc2, "runtime");

            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form);
            if (resp.StatusCode != HttpStatusCode.Created)
            {
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
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
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc3 = new StringContent("NoSuchMethodName");
            using var sc4 = new StringContent("dotnetcore");
            form.Add(sc3, "name");
            form.Add(sc4, "runtime");

            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form);
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
        public async Task ScheduleSetAndClearWorks()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            // Register function
            var path = GetSampleDllPath();
            using var form = new MultipartFormDataContent();
            using FileStream stream = File.OpenRead(path);
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc11 = new StringContent("Echo");
            using var sc12 = new StringContent("dotnet");
            form.Add(sc11, "name");
            form.Add(sc12, "runtime");

            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(info);

            // Schedule with a valid cron
            var body = new { expression = "* * * * *" };
            HttpResponseMessage sch = await client.PostAsJsonAsync(new Uri($"/api/functions/{info!.Id}/schedule", UriKind.Relative), body);
            if (!sch.IsSuccessStatusCode)
            {
                _output.WriteLine("Schedule body: " + await sch.Content.ReadAsStringAsync());
            }
            Assert.True(sch.IsSuccessStatusCode);
            BlobInfo? after = await sch.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(after);
            Assert.Contains(after!.Metadata, m => m.Name == "TimerTrigger" && m.Value == "* * * * *");

            // Clear schedule
            HttpResponseMessage del = await client.DeleteAsync(new Uri($"/api/functions/{info.Id}/schedule", UriKind.Relative));
            Assert.True(del.IsSuccessStatusCode);
            BlobInfo? cleared = await del.Content.ReadFromJsonAsync<BlobInfo>();
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
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc13 = new StringContent("Echo");
            using var sc14 = new StringContent("dotnet");
            form.Add(sc13, "name");
            form.Add(sc14, "runtime");
            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
            Assert.NotNull(info);

            // Attempt schedule with invalid expression
            var body = new { expression = "invalid cron" };
            HttpResponseMessage sch = await client.PostAsJsonAsync(new Uri($"/api/functions/{info!.Id}/schedule", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.BadRequest, sch.StatusCode);
            var err = await sch.Content.ReadAsStringAsync();
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
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc5 = new StringContent("Echo");
            using var sc6 = new StringContent("dotnet");
            using var sc7 = new StringContent("* * * * *");
            form.Add(sc5, "name");
            form.Add(sc6, "runtime");
            form.Add(sc7, "cron");

            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register/scheduled", UriKind.Relative), form);
            if (resp.StatusCode != HttpStatusCode.Created)
            {
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            BlobInfo? info = await resp.Content.ReadFromJsonAsync<BlobInfo>();
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
            using var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            using var sc8 = new StringContent("Echo");
            using var sc9 = new StringContent("dotnet");
            using var sc10 = new StringContent("not a cron");
            form.Add(sc8, "name");
            form.Add(sc9, "runtime");
            form.Add(sc10, "cron");

            HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register/scheduled", UriKind.Relative), form);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Invalid NCRONTAB", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ListFunctionsReturnsOnlyFunctionEntries()
        {
            CleanupStorage();
            HttpClient client = _factory.CreateClient();

            // Upload a non-function blob
            using (var form = new MultipartFormDataContent())
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello"));
                using var file = new StreamContent(ms);
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                form.Add(file, "file", "plain.txt");
                var blobResp = await client.PostAsync(new Uri("/api/blobs", UriKind.Relative), form);
                Assert.Equal(HttpStatusCode.Created, blobResp.StatusCode);
                var plain = await blobResp.Content.ReadFromJsonAsync<BlobInfo>();
                Assert.NotNull(plain);
            }

            // Register a function
            var path = GetSampleDllPath();
            using (var form2 = new MultipartFormDataContent())
            {
                using FileStream stream = File.OpenRead(path);
                using var file = new StreamContent(stream);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form2.Add(file, "file", Path.GetFileName(path));
                using var sc15 = new StringContent("Echo");
                using var sc16 = new StringContent("dotnet");
                form2.Add(sc15, "name");
                form2.Add(sc16, "runtime");

                HttpResponseMessage resp = await client.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form2);
                Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
                var fn = await resp.Content.ReadFromJsonAsync<BlobInfo>();
                Assert.NotNull(fn);
            }

            // List functions and ensure only the function entry appears
            HttpResponseMessage listResp = await client.GetAsync(new Uri("/api/functions", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var list = await listResp.Content.ReadFromJsonAsync<List<BlobInfo>>();
            Assert.NotNull(list);
            Assert.NotEmpty(list);
            Assert.All(list!, item => Assert.Contains(item.Metadata, m => m.Name == "function.name"));
        }
    }
}
