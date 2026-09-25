using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Ecanakli.SaveSystem;
using Newtonsoft.Json.Linq;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using PlayerData = Unity.Services.CloudSave.Models.Data.Player;

namespace Ecanakli.SaveSystem.UnityCloudSave
{
    /// <summary>ICloudSaveProvider over the UGS Cloud Save player data API. Never throws apart from cancellation.</summary>
    public sealed class UnityCloudSaveProvider : ICloudSaveProvider
    {
        private readonly UnityCloudSaveOptions _options;

        public UnityCloudSaveProvider(UnityCloudSaveOptions options = null)
        {
            _options = options ?? new UnityCloudSaveOptions();
            Capabilities = _options.ToCapabilities();
        }

        public CloudCapabilities Capabilities { get; }

        public string SignedInAccountId
        {
            get
            {
                try
                {
                    return AuthenticationService.Instance.IsSignedIn ? AuthenticationService.Instance.PlayerId : null;
                }
                catch (Exception)
                {
                    // Core/Authentication not initialized yet; behave like signed out rather than throwing.
                    return null;
                }
            }
        }

        public async UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int count = requests == null ? 0 : requests.Count;
            var results = new CloudReadResult[count];
            if (count == 0)
            {
                return results;
            }

            var byAccess = new Dictionary<AccessClass, List<int>>();
            for (int i = 0; i < count; i++)
            {
                CloudReadRequest request = requests[i];
                AccessClass accessClass = UnityCloudSaveAccessResolver.ResolveRead(request.Access, _options.IsPublicKey(request.Key));
                AddToGroup(byAccess, accessClass, i);
            }

            foreach (KeyValuePair<AccessClass, List<int>> group in byAccess)
            {
                await ReadGroupAsync(group.Key, group.Value, requests, results, ct);
            }

            return results;
        }

        public async UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int count = requests == null ? 0 : requests.Count;
            var results = new CloudWriteResult[count];
            if (count == 0)
            {
                return results;
            }

            var byAccess = new Dictionary<AccessClass, List<int>>();
            for (int i = 0; i < count; i++)
            {
                CloudWriteRequest request = requests[i];
                if (request.Access == CloudAccess.ServerOwned && !_options.IsPublicKey(request.Key))
                {
                    // The core must never route CloudReadOnly (server-owned) slots here; guard anyway.
                    results[i] = CloudWriteResult.Failed(request.Key, new CloudError(CloudErrorKind.Permanent,
                        "UGS Cloud Save cannot write a Protected (server-owned) key from the client: '" + request.Key + "'."));
                    continue;
                }

                AccessClass accessClass = UnityCloudSaveAccessResolver.ResolveWrite(_options.IsPublicKey(request.Key));
                AddToGroup(byAccess, accessClass, i);
            }

            foreach (KeyValuePair<AccessClass, List<int>> group in byAccess)
            {
                await WriteGroupAsync(group.Key, group.Value, requests, results, ct);
            }

            for (int i = 0; i < count; i++)
            {
                if (results[i] == null)
                {
                    results[i] = CloudWriteResult.Failed(requests[i].Key,
                        new CloudError(CloudErrorKind.Permanent, "No write result was produced (duplicate key in the same batch?)."));
                }
            }

            return results;
        }

        public async UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                AccessClass accessClass = UnityCloudSaveAccessResolver.ResolveWrite(_options.IsPublicKey(key));
                PlayerData.WriteAccessClassOptions accessOptions = accessClass == AccessClass.Public
                    ? new PlayerData.PublicWriteAccessClassOptions()
                    : (PlayerData.WriteAccessClassOptions)new PlayerData.DefaultWriteAccessClassOptions();
                var options = new PlayerData.DeleteOptions(accessOptions) { WriteLock = expectedVersion };

                await CloudSaveService.Instance.Data.Player.DeleteAsync(key, options).AsUniTask().AttachExternalCancellation(ct);

                // UGS reports success whether or not the key existed; there is no separate not-found signal here.
                return CloudDeleteResult.Deleted(key);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return CloudDeleteResult.Failed(key, UnityCloudSaveErrorMapper.Map(exception));
            }
        }

        private static void AddToGroup(Dictionary<AccessClass, List<int>> groups, AccessClass accessClass, int index)
        {
            if (!groups.TryGetValue(accessClass, out List<int> list))
            {
                list = new List<int>();
                groups[accessClass] = list;
            }

            list.Add(index);
        }

        private async UniTask ReadGroupAsync(
            AccessClass accessClass,
            List<int> indices,
            IReadOnlyList<CloudReadRequest> requests,
            CloudReadResult[] results,
            CancellationToken ct)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < indices.Count; i++)
            {
                keys.Add(requests[indices[i]].Key);
            }

            try
            {
                var options = new PlayerData.LoadOptions(ReadAccessOptionsFor(accessClass));
                Dictionary<string, Item> items = await CloudSaveService.Instance.Data.Player
                    .LoadAsync(keys, options).AsUniTask().AttachExternalCancellation(ct);

                for (int i = 0; i < indices.Count; i++)
                {
                    int index = indices[i];
                    string key = requests[index].Key;
                    if (items.TryGetValue(key, out Item item))
                    {
                        // GetAsString() returns the compact JSON text of the stored value (an object, not an
                        // escaped string), matching the byte[] UTF-8 JSON contract of CloudReadResult.Found.
                        byte[] bytes = Encoding.UTF8.GetBytes(item.Value.GetAsString());
                        results[index] = CloudReadResult.Found(key, bytes, item.WriteLock);
                    }
                    else
                    {
                        results[index] = CloudReadResult.NotFound(key);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CloudError error = UnityCloudSaveErrorMapper.Map(exception);
                for (int i = 0; i < indices.Count; i++)
                {
                    int index = indices[i];
                    results[index] = CloudReadResult.Failed(requests[index].Key, error);
                }
            }
        }

        private async UniTask WriteGroupAsync(
            AccessClass accessClass,
            List<int> indices,
            IReadOnlyList<CloudWriteRequest> requests,
            CloudWriteResult[] results,
            CancellationToken ct)
        {
            var data = new Dictionary<string, SaveItem>(StringComparer.Ordinal);
            for (int i = 0; i < indices.Count; i++)
            {
                CloudWriteRequest request = requests[indices[i]];
                JToken token;
                try
                {
                    // Must parse to a JToken, not pass the raw string: SaveItem stores 'object' through
                    // JToken.FromObject, and a plain C# string would be written back as an escaped JSON string.
                    token = JToken.Parse(Encoding.UTF8.GetString(request.Value));
                }
                catch (Exception exception)
                {
                    results[indices[i]] = CloudWriteResult.Failed(request.Key,
                        new CloudError(CloudErrorKind.Permanent, "Value for '" + request.Key + "' is not valid JSON.", null, exception));
                    continue;
                }

                data[request.Key] = new SaveItem(token, request.ExpectedVersion);
            }

            if (data.Count == 0)
            {
                return;
            }

            try
            {
                var options = new PlayerData.SaveOptions(WriteAccessOptionsFor(accessClass));
                Dictionary<string, string> saved = await CloudSaveService.Instance.Data.Player
                    .SaveAsync(data, options).AsUniTask().AttachExternalCancellation(ct);

                for (int i = 0; i < indices.Count; i++)
                {
                    int index = indices[i];
                    if (results[index] != null)
                    {
                        continue;
                    }

                    string key = requests[index].Key;
                    results[index] = saved.TryGetValue(key, out string newVersion)
                        ? CloudWriteResult.Succeeded(key, newVersion)
                        : CloudWriteResult.Failed(key, new CloudError(CloudErrorKind.Permanent, "UGS did not return a write lock for '" + key + "'."));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (CloudSaveConflictException conflict)
            {
                // A batch SaveAsync fails atomically; split named (actually conflicting) keys from co-batched siblings.
                HashSet<string> namedKeys = CollectKeys(conflict.Details, detail => detail.Key);
                UnityCloudSaveBatchResultSplitter.Apply(indices, requests, namedKeys, UnityCloudSaveErrorMapper.Map(conflict), results);
            }
            catch (CloudSaveValidationException validation)
            {
                HashSet<string> namedKeys = CollectKeys(validation.Details, detail => detail.Key);
                UnityCloudSaveBatchResultSplitter.Apply(indices, requests, namedKeys, UnityCloudSaveErrorMapper.Map(validation), results);
            }
            catch (Exception exception)
            {
                // Uniform failure (network, auth, rate limit): every key in the group gets the same classified error.
                UnityCloudSaveBatchResultSplitter.Apply(indices, requests, null, UnityCloudSaveErrorMapper.Map(exception), results);
            }
        }

        private static HashSet<string> CollectKeys<T>(List<T> details, Func<T, string> keySelector)
        {
            if (details == null || details.Count == 0)
            {
                return null;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < details.Count; i++)
            {
                string key = keySelector(details[i]);
                if (key != null)
                {
                    keys.Add(key);
                }
            }

            return keys.Count > 0 ? keys : null;
        }

        private static PlayerData.ReadAccessClassOptions ReadAccessOptionsFor(AccessClass accessClass)
        {
            switch (accessClass)
            {
                case AccessClass.Protected:
                    return new PlayerData.ProtectedReadAccessClassOptions();
                case AccessClass.Public:
                    return new PlayerData.PublicReadAccessClassOptions();
                default:
                    return new PlayerData.DefaultReadAccessClassOptions();
            }
        }

        private static PlayerData.WriteAccessClassOptions WriteAccessOptionsFor(AccessClass accessClass)
        {
            return accessClass == AccessClass.Public
                ? new PlayerData.PublicWriteAccessClassOptions()
                : (PlayerData.WriteAccessClassOptions)new PlayerData.DefaultWriteAccessClassOptions();
        }
    }
}
