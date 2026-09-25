using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Null Object provider used when there is no backend. Always signed out.</summary>
    public sealed class NullCloudSaveProvider : ICloudSaveProvider
    {
        public static readonly NullCloudSaveProvider Instance = new NullCloudSaveProvider();

        private static readonly CloudError NotSignedInError = new CloudError(CloudErrorKind.NotSignedIn, "No cloud provider is configured.");

        private NullCloudSaveProvider()
        {
            Capabilities = new CloudCapabilities(1, 1, 1, 0, false);
        }

        public CloudCapabilities Capabilities { get; }

        public string SignedInAccountId => null;

        public UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int count = requests == null ? 0 : requests.Count;
            var results = new CloudReadResult[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = CloudReadResult.Failed(requests[i].Key, NotSignedInError);
            }

            return UniTask.FromResult<IReadOnlyList<CloudReadResult>>(results);
        }

        public UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int count = requests == null ? 0 : requests.Count;
            var results = new CloudWriteResult[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = CloudWriteResult.Failed(requests[i].Key, NotSignedInError);
            }

            return UniTask.FromResult<IReadOnlyList<CloudWriteResult>>(results);
        }

        public UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return UniTask.FromResult(CloudDeleteResult.Failed(key, NotSignedInError));
        }
    }
}
