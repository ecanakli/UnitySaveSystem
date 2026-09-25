using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Ordered hook run after slots load (ProfileActivated) or after a cloud restore (CloudRestore).</summary>
    public interface IRestoreListener
    {
        /// <summary>Ascending dispatch order; ties resolve by registration order.</summary>
        int Order { get; }

        /// <summary>Rebuild caches and views from current slot state; never replay earned effects.</summary>
        UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct);
    }
}
