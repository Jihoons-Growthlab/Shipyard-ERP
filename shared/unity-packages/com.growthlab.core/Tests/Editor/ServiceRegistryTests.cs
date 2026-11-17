using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace GrowthLab.Core.Tests
{
    public sealed class ServiceRegistryTests
    {
        [Test]
        public void Register_ThenGet_ReturnsSameInstanceByContract()
        {
            var registry = new ServiceRegistry();
            var bus = new EventBus();
            registry.Register<IEventBus>(bus);

            Assert.AreSame(bus, registry.Get<IEventBus>());
            Assert.IsFalse(registry.Contains<EventBus>(), "Lookup is by registered contract type only.");
        }

        [Test]
        public void DuplicateRegistration_Throws()
        {
            var registry = new ServiceRegistry();
            registry.Register<IEventBus>(new EventBus());
            Assert.Throws<InvalidOperationException>(() => registry.Register<IEventBus>(new EventBus()));
        }

        [Test]
        public void MissingService_GetThrows_TryGetReturnsFalse()
        {
            var registry = new ServiceRegistry();
            Assert.Throws<KeyNotFoundException>(() => registry.Get<IEventBus>());
            Assert.IsFalse(registry.TryGet<IEventBus>(out var service));
            Assert.IsNull(service);
        }

        [Test]
        public void Unregister_RemovesService()
        {
            var registry = new ServiceRegistry();
            registry.Register<IEventBus>(new EventBus());
            Assert.IsTrue(registry.Unregister<IEventBus>());
            Assert.IsFalse(registry.Contains<IEventBus>());
        }
    }
}
