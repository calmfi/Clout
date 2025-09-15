using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Cloud.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Host.IntegrationTests
{
    [Collection("Integration.Blobs")] // serialize to avoid storage collisions
    public class BlobsTests(IntegrationTestFactory factory, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<IntegrationTestFactory>
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

        [Fact]
        public async Task ListEmptyInitially()
        {
            CleanupStorage();

            HttpClient client = _factory.CreateClient();
            HttpResponseMessage resp = await client.GetAsync("/api/blobs").ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            List<BlobInfo>? list = await resp.Content.ReadFromJsonAsync<List<BlobInfo>>().ConfigureAwait(false);
            Assert.NotNull(list);
            Assert.Empty(list!);
        }

        [Fact]
        public async Task UploadInfoDownloadDelete()
        {
            CleanupStorage();

            HttpClient client = _factory.CreateClient();

            // upload
            var payload = Encoding.UTF8.GetBytes("hello world");
            using var form = new MultipartFormDataContent();
            using var stream = new MemoryStream(payload);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", "hello.txt");
            HttpResponseMessage up = await client.PostAsync("/api/blobs", form).ConfigureAwait(false);
            if (up.StatusCode != HttpStatusCode.Created)
            {
                _output.WriteLine("Upload body: " + await up.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.Equal(HttpStatusCode.Created, up.StatusCode);
            BlobInfo? info = await up.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);
            Assert.Equal("hello.txt", info!.FileName);

            // info
            BlobInfo? meta = await client.GetFromJsonAsync<BlobInfo>($"/api/blobs/{info.Id}/info").ConfigureAwait(false);
            Assert.NotNull(meta);
            Assert.Equal(info.Id, meta!.Id);

            // download
            HttpResponseMessage dl = await client.GetAsync($"/api/blobs/{info.Id}").ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
            var bytes = await dl.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Assert.Equal(payload, bytes);

            // delete
            HttpResponseMessage del = await client.DeleteAsync($"/api/blobs/{info.Id}").ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

            // verify not found after delete
            HttpResponseMessage after = await client.GetAsync($"/api/blobs/{info.Id}/info").ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }

        [Fact]
        public async Task UpdateMetadataReplacesList()
        {
            CleanupStorage();

            HttpClient client = _factory.CreateClient();

            // upload minimal file
            var payload = Encoding.UTF8.GetBytes("m");
            using var form = new MultipartFormDataContent();
            using var stream = new MemoryStream(payload);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", "meta.txt");
            HttpResponseMessage up = await client.PostAsync("/api/blobs", form).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Created, up.StatusCode);
            BlobInfo? info = await up.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(info);

            // set metadata
            var newMeta = new List<BlobMetadata>
            {
                new("author", "text/plain", "alice"),
                new("tags", "application/json", "[\"a\",\"b\"]"),
            };
            HttpResponseMessage put = await client.PutAsJsonAsync($"/api/blobs/{info!.Id}/metadata", newMeta).ConfigureAwait(false);
            if (!put.IsSuccessStatusCode)
            {
                var body = await put.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.Fail($"Failed to set metadata: {put.StatusCode} {body}");
            }
            BlobInfo? after = await put.Content.ReadFromJsonAsync<BlobInfo>().ConfigureAwait(false);
            Assert.NotNull(after);
            Assert.Equal(2, after!.Metadata.Count);
            Assert.Contains(after.Metadata, m => m.Name == "author" && m.Value == "alice" && m.ContentType == "text/plain");
            Assert.Contains(after.Metadata, m => m.Name == "tags" && m.ContentType == "application/json");

            // verify via info endpoint
            BlobInfo? meta = await client.GetFromJsonAsync<BlobInfo>($"/api/blobs/{info.Id}/info").ConfigureAwait(false);
            Assert.NotNull(meta);
            Assert.Equal(2, meta!.Metadata.Count);
        }
    }
}
