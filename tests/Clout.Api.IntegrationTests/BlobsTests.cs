using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System;
using Cloud.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Api.IntegrationTests;

[Collection("Integration.Blobs")] // serialize to avoid storage collisions
public class BlobsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public BlobsTests(WebApplicationFactory<Program> factory, Xunit.Abstractions.ITestOutputHelper output)
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

    [Fact]
    public async Task List_EmptyInitially()
    {
        CleanupStorage();

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/blobs");
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            _output.WriteLine("Body: " + await resp.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<BlobInfo>>();
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Upload_Info_Download_Delete()
    {
        CleanupStorage();

        var client = _factory.CreateClient();

        // upload
        var payload = Encoding.UTF8.GetBytes("hello world");
        using var form = new MultipartFormDataContent();
        using var stream = new MemoryStream(payload);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "hello.txt");
        var up = await client.PostAsync("/api/blobs", form);
        if (up.StatusCode != HttpStatusCode.Created)
        {
            _output.WriteLine("Upload body: " + await up.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var info = await up.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(info);
        Assert.Equal("hello.txt", info!.FileName);

        // info
        var meta = await client.GetFromJsonAsync<BlobInfo>($"/api/blobs/{info.Id}/info");
        Assert.NotNull(meta);
        Assert.Equal(info.Id, meta!.Id);

        // download
        var dl = await client.GetAsync($"/api/blobs/{info.Id}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        var bytes = await dl.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, bytes);

        // delete
        var del = await client.DeleteAsync($"/api/blobs/{info.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // verify not found after delete
        var after = await client.GetAsync($"/api/blobs/{info.Id}/info");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Update_Metadata_ReplacesList()
    {
        CleanupStorage();

        var client = _factory.CreateClient();

        // upload minimal file
        var payload = Encoding.UTF8.GetBytes("m");
        using var form = new MultipartFormDataContent();
        using var stream = new MemoryStream(payload);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "meta.txt");
        var up = await client.PostAsync("/api/blobs", form);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var info = await up.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(info);

        // set metadata
        var newMeta = new List<BlobMetadata>
        {
            new("author", "text/plain", "alice"),
            new("tags", "application/json", "[\"a\",\"b\"]"),
        };
        var put = await client.PutAsJsonAsync($"/api/blobs/{info!.Id}/metadata", newMeta);
        if (!put.IsSuccessStatusCode)
        {
            var body = await put.Content.ReadAsStringAsync();
            Assert.True(false, $"Failed to set metadata: {put.StatusCode} {body}");
        }
        var after = await put.Content.ReadFromJsonAsync<BlobInfo>();
        Assert.NotNull(after);
        Assert.Equal(2, after!.Metadata.Count);
        Assert.Contains(after.Metadata, m => m.Name == "author" && m.Value == "alice" && m.ContentType == "text/plain");
        Assert.Contains(after.Metadata, m => m.Name == "tags" && m.ContentType == "application/json");

        // verify via info endpoint
        var meta = await client.GetFromJsonAsync<BlobInfo>($"/api/blobs/{info.Id}/info");
        Assert.NotNull(meta);
        Assert.Equal(2, meta!.Metadata.Count);
    }
}
