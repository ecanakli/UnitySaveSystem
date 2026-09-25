using System.Collections.Generic;
using System.Text;
using Ecanakli.SaveSystem;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.UnityCloudSave.Tests
{
    /// <summary>UGS batch writes fail atomically; named keys get the real error, siblings get a retryable one.</summary>
    [TestFixture]
    public sealed class UnityCloudSaveBatchResultSplitterTests
    {
        private static readonly byte[] Value = Encoding.UTF8.GetBytes("{}");

        [Test]
        public void Apply_NamedKey_GetsSpecificError_OthersGetRetryableSibling()
        {
            var requests = new List<CloudWriteRequest>
            {
                new CloudWriteRequest("a", Value, null, CloudAccess.ClientOwned),
                new CloudWriteRequest("b", Value, null, CloudAccess.ClientOwned),
                new CloudWriteRequest("c", Value, null, CloudAccess.ClientOwned),
            };
            var indices = new List<int> { 0, 1, 2 };
            var namedKeys = new HashSet<string> { "b" };
            var namedError = new CloudError(CloudErrorKind.Conflict, "write lock mismatch");
            var results = new CloudWriteResult[3];

            UnityCloudSaveBatchResultSplitter.Apply(indices, requests, namedKeys, namedError, results);

            Assert.That(results[0].Error.Kind, Is.EqualTo(CloudErrorKind.Transient), "sibling 'a' is retried alone");
            Assert.That(results[1].Error.Kind, Is.EqualTo(CloudErrorKind.Conflict), "the actually conflicting key");
            Assert.That(results[2].Error.Kind, Is.EqualTo(CloudErrorKind.Transient), "sibling 'c' is retried alone");
            Assert.That(results[0].Error.IsRetryable, Is.True);
            Assert.That(results[1].Error.IsRetryable, Is.False, "a real conflict must not be retried blindly");
        }

        [Test]
        public void Apply_NoNamedKeys_EveryKeyGetsTheSameError()
        {
            var requests = new List<CloudWriteRequest>
            {
                new CloudWriteRequest("a", Value, null, CloudAccess.ClientOwned),
                new CloudWriteRequest("b", Value, null, CloudAccess.ClientOwned),
            };
            var indices = new List<int> { 0, 1 };
            var uniformError = new CloudError(CloudErrorKind.Transient, "network error");
            var results = new CloudWriteResult[2];

            UnityCloudSaveBatchResultSplitter.Apply(indices, requests, null, uniformError, results);

            Assert.That(results[0].Error, Is.SameAs(uniformError));
            Assert.That(results[1].Error, Is.SameAs(uniformError));
        }

        [Test]
        public void Apply_SkipsIndicesThatAlreadyHaveAResult()
        {
            var requests = new List<CloudWriteRequest>
            {
                new CloudWriteRequest("a", Value, null, CloudAccess.ClientOwned),
                new CloudWriteRequest("b", Value, null, CloudAccess.ClientOwned),
            };
            var indices = new List<int> { 0, 1 };
            var results = new CloudWriteResult[2];
            CloudWriteResult preFilled = CloudWriteResult.Succeeded("a", "v1");
            results[0] = preFilled;

            UnityCloudSaveBatchResultSplitter.Apply(indices, requests, null, new CloudError(CloudErrorKind.Permanent, "x"), results);

            Assert.That(results[0], Is.SameAs(preFilled), "an already-populated result is never overwritten");
            Assert.That(results[1], Is.Not.Null);
        }
    }
}
