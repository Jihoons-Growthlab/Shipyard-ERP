using System.Threading;
using System.Threading.Tasks;

namespace GrowthLab.Core
{
    /// <summary>A unit started by <see cref="InitializationPipeline"/> in registration order.</summary>
    public interface IInitializable
    {
        Task InitializeAsync(CancellationToken cancellationToken);
    }

    /// <summary>A unit stopped by <see cref="InitializationPipeline"/> in reverse registration order.</summary>
    public interface IShutdownable
    {
        void Shutdown();
    }
}
