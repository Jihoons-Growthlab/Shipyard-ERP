using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace GrowthLab.Core.Tests
{
    public sealed class EventBusTests
    {
        private struct Ping
        {
            public int Value;
        }

        private struct Pong
        {
        }

        [Test]
        public void Publish_DeliversOnlyToMatchingType()
        {
            var bus = new EventBus();
            var pings = new List<int>();
            var pongs = 0;
            bus.Subscribe<Ping>(p => pings.Add(p.Value));
            bus.Subscribe<Pong>(_ => pongs++);

            bus.Publish(new Ping { Value = 7 });

            CollectionAssert.AreEqual(new[] { 7 }, pings);
            Assert.AreEqual(0, pongs);
        }

        [Test]
        public void Dispose_Unsubscribes_AndIsIdempotent()
        {
            var bus = new EventBus();
            var calls = 0;
            var handle = bus.Subscribe<Ping>(_ => calls++);

            handle.Dispose();
            handle.Dispose();
            bus.Publish(new Ping());

            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, bus.SubscriberCount<Ping>());
        }

        [Test]
        public void UnsubscribeDuringPublish_StopsLaterHandlerImmediately()
        {
            var bus = new EventBus();
            var secondCalls = 0;
            IDisposable second = null;
            bus.Subscribe<Ping>(_ => second.Dispose());
            second = bus.Subscribe<Ping>(_ => secondCalls++);

            bus.Publish(new Ping());

            Assert.AreEqual(0, secondCalls);
        }

        [Test]
        public void SubscribeDuringPublish_ReceivesFromNextPublish()
        {
            var bus = new EventBus();
            var lateCalls = 0;
            var added = false;
            bus.Subscribe<Ping>(_ =>
            {
                if (!added)
                {
                    added = true;
                    bus.Subscribe<Ping>(__ => lateCalls++);
                }
            });

            bus.Publish(new Ping());
            Assert.AreEqual(0, lateCalls);

            bus.Publish(new Ping());
            Assert.AreEqual(1, lateCalls);
        }

        [Test]
        public void ThrowingHandler_IsLogged_AndOthersStillRun()
        {
            var log = new RecordingLog();
            var bus = new EventBus(log);
            var calls = 0;
            bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
            bus.Subscribe<Ping>(_ => calls++);

            bus.Publish(new Ping());

            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, log.Count(LogLevel.Error));
            Assert.IsInstanceOf<InvalidOperationException>(log.Entries[0].Exception);
        }

        [Test]
        public void Clear_DeactivatesHandles()
        {
            var bus = new EventBus();
            var calls = 0;
            var handle = bus.Subscribe<Ping>(_ => calls++);

            bus.Clear();
            bus.Subscribe<Ping>(_ => calls += 10);
            handle.Dispose();
            bus.Publish(new Ping());

            Assert.AreEqual(10, calls, "Disposing a cleared handle must not remove a newer subscription.");
        }

        [Test]
        public void Subscribe_NullHandler_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new EventBus().Subscribe<Ping>(null));
        }
    }
}
