using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace GrowthLab.Network.Tests
{
    /// <summary>
    /// Exercises the real UnityWebRequest path without any server: loopback port 1 is closed, so the
    /// connection is refused locally. Nothing leaves the machine.
    /// </summary>
    public sealed class UnityWebRequestTransportTests
    {
        private const string ClosedLoopbackUrl = "http://127.0.0.1:1/";

        [UnityTest]
        public IEnumerator RefusedConnection_ThrowsConnectionFailed()
        {
            var task = new UnityWebRequestTransport().SendAsync(new HttpRequest(HttpMethod.Get, ClosedLoopbackUrl),
                TimeSpan.FromSeconds(5), CancellationToken.None);

            yield return WaitFor(task);

            Assert.IsTrue(task.IsFaulted, "Status: " + task.Status);
            var error = task.Exception.InnerException as NetworkException;
            Assert.IsNotNull(error, task.Exception.ToString());
            Assert.AreEqual(NetworkErrorKind.ConnectionFailed, error.Kind);
        }

        [Test]
        public void AlreadyCancelled_DoesNotSend()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var task = new UnityWebRequestTransport().SendAsync(new HttpRequest(HttpMethod.Get, ClosedLoopbackUrl),
                    TimeSpan.FromSeconds(5), cts.Token);
                Assert.IsTrue(task.IsCanceled);
            }
        }

        private static IEnumerator WaitFor(Task task, double timeoutSeconds = 10)
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
