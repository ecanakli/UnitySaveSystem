using System.Collections.Generic;
using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.UnityCloudSave
{
    /// <summary>
    /// A UGS batch SaveAsync call fails atomically: one bad key aborts every key sent in that HTTP call.
    /// Named keys (the ones the SDK actually blamed) get the specific error; every other co-batched key gets a
    /// retryable sibling-failure so CloudGateway retries it alone instead of treating it as permanently failed.
    /// namedKeys == null means the whole batch failed uniformly (e.g. network/auth); every key gets namedError.
    /// </summary>
    internal static class UnityCloudSaveBatchResultSplitter
    {
        public static void Apply(
            IReadOnlyList<int> indices,
            IReadOnlyList<CloudWriteRequest> requests,
            ISet<string> namedKeys,
            CloudError namedError,
            CloudWriteResult[] results)
        {
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                if (results[index] != null)
                {
                    continue;
                }

                string key = requests[index].Key;
                bool isNamed = namedKeys == null || namedKeys.Contains(key);
                results[index] = isNamed
                    ? CloudWriteResult.Failed(key, namedError)
                    : CloudWriteResult.Failed(key, UnityCloudSaveErrorMapper.SiblingBatchFailure(key));
            }
        }
    }
}
