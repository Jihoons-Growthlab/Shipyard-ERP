using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace GrowthLab.Network.Tests
{
    public sealed class RestClientTests
    {
        [Serializable]
        public sealed class Item
        {
            public int Id;
            public string Name;
        }

        private List<TimeSpan> _delays;

        [SetUp]
        public void SetUp() => _delays = new List<TimeSpan>();

        private RestClient Client(FakeTransport transport, int maxRetries = 2, TimeSpan? timeout = null) =>
            new RestClient(transport, new RestClientOptions
            {
                BaseUrl = "https://api.example.test/v1/",
                MaxRetries = maxRetries,
                Timeout = timeout ?? TimeSpan.FromSeconds(10),
                RetryBaseDelay = TimeSpan.FromMilliseconds(100),
                MaxRetryDelay = TimeSpan.FromSeconds(1),
                Delay = (delay, _) =>
                {
                    _delays.Add(delay);
                    return Task.CompletedTask;
                },
            });

        private static NetworkException SendExpectingError(RestClient client, HttpRequest request) =>
            Assert.Throws<NetworkException>(() => client.SendAsync(request).GetAwaiter().GetResult());

        [Test]
        public void RelativeUrl_UsesBaseUrl_AbsoluteUrlIsKept()
        {
            var transport = new FakeTransport().Respond(200).Respond(200);
            var client = Client(transport);

            client.SendAsync(new HttpRequest(HttpMethod.Get, "/items")).GetAwaiter().GetResult();
            client.SendAsync(new HttpRequest(HttpMethod.Get, "https://other.test/x")).GetAwaiter().GetResult();

            Assert.AreEqual("https://api.example.test/v1/items", transport.Requests[0].Url);
            Assert.AreEqual("https://other.test/x", transport.Requests[1].Url);
        }

        [TestCase(400, NetworkErrorKind.BadRequest)]
        [TestCase(401, NetworkErrorKind.Unauthorized)]
        [TestCase(403, NetworkErrorKind.Forbidden)]
        [TestCase(404, NetworkErrorKind.NotFound)]
        [TestCase(409, NetworkErrorKind.Conflict)]
        [TestCase(418, NetworkErrorKind.ClientError)]
        [TestCase(422, NetworkErrorKind.BadRequest)]
        [TestCase(500, NetworkErrorKind.ServerError)]
        [TestCase(304, NetworkErrorKind.Unexpected)]
        public void Status_MapsToErrorKind_WithoutRetry(int status, NetworkErrorKind expected)
        {
            var transport = new FakeTransport().Respond(status, "{\"error\":\"x\"}");
            var error = SendExpectingError(Client(transport), new HttpRequest(HttpMethod.Get, "items"));

            Assert.AreEqual(expected, error.Kind);
            Assert.AreEqual(status, error.StatusCode);
            Assert.AreEqual("{\"error\":\"x\"}", error.Response.Body);
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public void SafeMethod_RetriesTransientFailures_WithBackoff_ThenSucceeds()
        {
            var transport = new FakeTransport()
                .Fail(NetworkErrorKind.ConnectionFailed)
                .Respond(503)
                .Respond(200, "ok");

            var response = Client(transport).SendAsync(new HttpRequest(HttpMethod.Get, "items")).GetAwaiter().GetResult();

            Assert.AreEqual("ok", response.Body);
            Assert.AreEqual(3, transport.Requests.Count);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, _delays);
        }

        [Test]
        public void SafeMethod_StopsAfterMaxRetries_AndReportsAttempts()
        {
            var transport = new FakeTransport().Respond(502).Respond(502).Respond(502);
            var error = SendExpectingError(Client(transport, maxRetries: 2), new HttpRequest(HttpMethod.Head, "items"));

            Assert.AreEqual(NetworkErrorKind.ServerError, error.Kind);
            Assert.AreEqual(3, error.Attempts);
        }

        [TestCase(HttpMethod.Post)]
        [TestCase(HttpMethod.Put)]
        [TestCase(HttpMethod.Patch)]
        [TestCase(HttpMethod.Delete)]
        public void UnsafeMethod_IsNeverRetried(HttpMethod method)
        {
            var transport = new FakeTransport().Respond(503).Respond(200);
            var error = SendExpectingError(Client(transport), new HttpRequest(method, "items") { Body = "{}" });

            Assert.AreEqual(1, transport.Requests.Count);
            Assert.AreEqual(1, error.Attempts);
            CollectionAssert.IsEmpty(_delays);
        }

        [Test]
        public void RetryAfterHeader_IsHonoured_AndCapped()
        {
            var transport = new FakeTransport()
                .Respond(429, null, new Dictionary<string, string> { { "Retry-After", "60" } })
                .Respond(200);

            Client(transport).SendAsync(new HttpRequest(HttpMethod.Get, "items")).GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, _delays);
        }

        [Test]
        public void InternalServerError500_IsNotRetried()
        {
            var transport = new FakeTransport().Respond(500).Respond(200);
            SendExpectingError(Client(transport), new HttpRequest(HttpMethod.Get, "items"));
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public void CallerCancellation_BeforeSend_ThrowsCancelled_NotNetworkError()
        {
            var transport = new FakeTransport().Respond(200);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Catch<OperationCanceledException>(() =>
                    Client(transport).SendAsync(new HttpRequest(HttpMethod.Get, "items"), cts.Token).GetAwaiter().GetResult());
            }

            CollectionAssert.IsEmpty(transport.Requests);
        }

        [Test]
        public void CallerCancellation_DuringSend_IsNotRetried_AndNotMappedToTimeout()
        {
            using (var cts = new CancellationTokenSource())
            {
                var transport = new FakeTransport().Then((_, token) =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new HttpResponse(200));
                });

                Assert.Catch<OperationCanceledException>(() =>
                    Client(transport).SendAsync(new HttpRequest(HttpMethod.Get, "items"), cts.Token).GetAwaiter().GetResult());
                Assert.AreEqual(1, transport.Requests.Count);
            }
        }

        [UnityTest]
        public IEnumerator Timeout_IsReported_AndRetriedForSafeMethod()
        {
            var transport = new FakeTransport().Hang().Respond(200, "late ok");
            var task = Client(transport, timeout: TimeSpan.FromMilliseconds(50))
                .SendAsync(new HttpRequest(HttpMethod.Get, "items"));

            yield return WaitFor(task);

            Assert.AreEqual("late ok", task.Result.Body);
            Assert.AreEqual(2, transport.Requests.Count);
        }

        [UnityTest]
        public IEnumerator Timeout_OnUnsafeMethod_ThrowsTimeout()
        {
            var transport = new FakeTransport().Hang();
            var task = Client(transport, timeout: TimeSpan.FromMilliseconds(50))
                .SendAsync(new HttpRequest(HttpMethod.Post, "items") { Body = "{}" });

            yield return WaitFor(task);

            Assert.IsTrue(task.IsFaulted);
            var error = task.Exception.InnerException as NetworkException;
            Assert.IsNotNull(error);
            Assert.AreEqual(NetworkErrorKind.Timeout, error.Kind);
        }

        [Test]
        public void GetJson_ParsesBody_AndInvalidJsonThrowsFormatException()
        {
            var transport = new FakeTransport()
                .Respond(200, "{\"Id\":5,\"Name\":\"pump\"}")
                .Respond(200, "<html>");
            var client = Client(transport);

            var item = client.GetJsonAsync<Item>("items/5").GetAwaiter().GetResult();
            Assert.AreEqual(5, item.Id);
            Assert.AreEqual("pump", item.Name);

            Assert.Throws<FormatException>(() => client.GetJsonAsync<Item>("items/6").GetAwaiter().GetResult());
        }

        [Test]
        public void SendJson_SerializesBody()
        {
            var transport = new FakeTransport().Respond(201, "{\"Id\":1}");
            var created = Client(transport)
                .SendJsonAsync<Item, Item>(HttpMethod.Post, "items", new Item { Name = "valve" })
                .GetAwaiter().GetResult();

            StringAssert.Contains("\"Name\":\"valve\"", transport.Requests[0].Body);
            Assert.AreEqual(1, created.Id);
        }

        private static IEnumerator WaitFor(Task task, double timeoutSeconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Task did not complete within " + timeoutSeconds + "s.");
                }

                yield return null;
            }
        }
    }
}
