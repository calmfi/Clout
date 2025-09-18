using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clout.Host.IntegrationTests
{
    [Collection("Integration.Queue")]
    public class QueueTests(IntegrationTestFactory factory, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<IntegrationTestFactory>
    {
        private readonly WebApplicationFactory<Program> _factory = factory;
        private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

        private static void CleanupQueueData()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "queue-data");
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private sealed record QueueStats(string Name, int MessageCount, long TotalBytes);

        [Fact]
        public async Task HealthEndpointReturnsOk()
        {
            CleanupQueueData();
            using HttpClient client = _factory.CreateClient();

            var resp = await client.GetAsync(new Uri("/health", UriKind.Relative));
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                _output.WriteLine("Body: " + body);
            }
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("OK\n", body);
        }

        [Fact]
        public async Task CreateQueueThenListShowsQueue()
        {
            CleanupQueueData();
            using HttpClient client = _factory.CreateClient();
            var name = "q-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var create = await client.PostAsync(new Uri($"/amqp/queues/{name}", UriKind.Relative), content: null);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var statsResp = await client.GetAsync(new Uri("/amqp/queues", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, statsResp.StatusCode);
            var list = await statsResp.Content.ReadFromJsonAsync<List<QueueStats>>();
            Assert.NotNull(list);
            Assert.Contains(list!, s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.MessageCount == 0);
        }

        [Fact]
        public async Task EnqueueThenDequeueRoundTripJson()
        {
            CleanupQueueData();
            using HttpClient client = _factory.CreateClient();
            var name = "q-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Explicitly create queue
            var create = await client.PostAsync(new Uri($"/amqp/queues/{name}", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            // Enqueue JSON message
            var payload = new { id = 123, text = "hello" };
            var enqueue = await client.PostAsJsonAsync(new Uri($"/amqp/queues/{name}/messages", UriKind.Relative), payload);
            if (enqueue.StatusCode != HttpStatusCode.Accepted)
            {
                _output.WriteLine("Enqueue body: " + await enqueue.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            // Stats should show one message
            var statsResp = await client.GetAsync(new Uri("/amqp/queues", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, statsResp.StatusCode);
            var stats = await statsResp.Content.ReadFromJsonAsync<List<QueueStats>>();
            Assert.NotNull(stats);
            Assert.Contains(stats!, s => s.Name == name && s.MessageCount == 1);

            // Dequeue
            var dequeue = await client.PostAsync(new Uri($"/amqp/queues/{name}/dequeue", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, dequeue.StatusCode);
            var json = await dequeue.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == 123);
            Assert.True(doc.RootElement.TryGetProperty("text", out var textProp) && textProp.GetString() == "hello");

            // Stats should now show zero
            var statsResp2 = await client.GetAsync(new Uri("/amqp/queues", UriKind.Relative));
            var stats2 = await statsResp2.Content.ReadFromJsonAsync<List<QueueStats>>();
            Assert.NotNull(stats2);
            Assert.Contains(stats2!, s => s.Name == name && s.MessageCount == 0);
        }

        [Fact]
        public async Task DequeueEmptyQueueWithTimeoutReturnsNoContent()
        {
            CleanupQueueData();
            using HttpClient client = _factory.CreateClient();
            var name = "empty-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            // Use a short timeoutMs to trigger cancellation quickly
            var dequeue = await client.PostAsync(new Uri($"/amqp/queues/{name}/dequeue?timeoutMs=25", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, dequeue.StatusCode);
        }

        [Fact]
        public async Task PurgeQueueRemovesMessages()
        {
            CleanupQueueData();
            using HttpClient client = _factory.CreateClient();
            var name = "purge-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            // Create queue and add two messages
            var create = await client.PostAsync(new Uri($"/amqp/queues/{name}", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            await client.PostAsJsonAsync(new Uri($"/amqp/queues/{name}/messages", UriKind.Relative), new { x = 1 });
            await client.PostAsJsonAsync(new Uri($"/amqp/queues/{name}/messages", UriKind.Relative), new { x = 2 });

            var statsPre = await client.GetFromJsonAsync<List<QueueStats>>(new Uri("/amqp/queues", UriKind.Relative));
            Assert.NotNull(statsPre);
            Assert.Contains(statsPre!, s => s.Name == name && s.MessageCount == 2);

            // Purge
            var purge = await client.PostAsync(new Uri($"/amqp/queues/{name}/purge", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);

            var statsPost = await client.GetFromJsonAsync<List<QueueStats>>(new Uri("/amqp/queues", UriKind.Relative));
            Assert.NotNull(statsPost);
            Assert.Contains(statsPost!, s => s.Name == name && s.MessageCount == 0);
        }
    }
}