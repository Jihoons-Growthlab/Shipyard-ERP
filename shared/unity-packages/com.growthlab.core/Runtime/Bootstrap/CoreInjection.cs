using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrowthLab.Core
{
    /// <summary>
    /// Implemented by scene components that need Core services. <see cref="CoreBootstrap"/> calls it once
    /// per scene load: for scenes loaded after Core is ready this happens before the components' Start;
    /// for scenes already open while Core initializes it happens right after initialization.
    /// </summary>
    public interface ICoreConsumer
    {
        void Construct(CoreContext context);
    }

    /// <summary>Explicit injection: finds <see cref="ICoreConsumer"/> components and passes the context.</summary>
    public static class CoreInjection
    {
        private const string Tag = "Inject";

        /// <returns>Number of components that received the context without throwing.</returns>
        public static int InjectScene(Scene scene, CoreContext context)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return 0;
            }

            var count = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                count += InjectHierarchy(root, context);
            }

            return count;
        }

        /// <summary>
        /// Injects every <see cref="ICoreConsumer"/> under <paramref name="root"/> (inactive ones included).
        /// A consumer that throws is logged and skipped; the others are still injected.
        /// </summary>
        /// <returns>Number of components that received the context without throwing.</returns>
        public static int InjectHierarchy(GameObject root, CoreContext context)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // A fresh list per call: Construct may itself instantiate objects and inject them.
            var consumers = new List<ICoreConsumer>();
            root.GetComponentsInChildren(true, consumers);
            var count = 0;
            foreach (var consumer in consumers)
            {
                try
                {
                    consumer.Construct(context);
                    count++;
                }
                catch (Exception exception)
                {
                    var name = consumer is Component component && component != null ? component.name : consumer.GetType().Name;
                    context.Log.Error(Tag, "Construct failed on " + name + " (" + consumer.GetType().Name + "); continuing.",
                        exception);
                }
            }

            return count;
        }
    }
}
