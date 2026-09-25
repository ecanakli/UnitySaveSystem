using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Backend port. Returns classified errors and never throws apart from cancellation.</summary>
    /// <remarks>
    /// A caller wrapping this provider (the package's CloudGateway) may abandon a call once it exceeds its own
    /// timeout, while the call keeps running because this contract does not require immediate cancellation. The
    /// caller then retries with a request that is byte-identical (same key, same value, same write id, same
    /// expected version), so the abandoned attempt and its retry can be concurrently in flight against the same
    /// key. Implementations must tolerate this: both calls must be safe to execute at the same time, and because
    /// their payloads are identical, the value the backend ends up storing must converge regardless of which one
    /// is applied last.
    /// </remarks>
    public interface ICloudSaveProvider
    {
        CloudCapabilities Capabilities { get; }

        /// <summary>Signed-in account id; null when signed out.</summary>
        string SignedInAccountId { get; }

        /// <summary>One result per request, in request order.</summary>
        UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct);

        /// <summary>One result per request, in request order.</summary>
        /// <remarks>An abandoned write and its retry may overlap for the same key; see the interface remarks.</remarks>
        UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct);

        /// <summary>Deletes a key; expectedVersion null means unconditional.</summary>
        /// <remarks>An abandoned delete and its retry may overlap for the same key; see the interface remarks.</remarks>
        UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct);
    }
}
