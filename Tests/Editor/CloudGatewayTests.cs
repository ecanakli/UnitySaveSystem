using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>CloudGateway chunking, prechecks, retry classification, conditional versions and cancellation.</summary>
    [TestFixture]
    public sealed class CloudGatewayTests
    {
        private const string AccountId = "account-1";

        private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        private FakeCloudSaveProvider _provider;
        private ManualSaveClock _clock;
        private TestSaveLogger _logger;
        private CloudGateway _gateway;

        [SetUp]
        public void SetUp()
        {
            _provider = new FakeCloudSaveProvider(signedInAccountId: AccountId);
            _clock = new ManualSaveClock();
            _logger = new TestSaveLogger();
            _gateway = CreateGateway(3);
        }

        // Chunking

        [TestCase(7, 3, 3)]
        [TestCase(6, 3, 2)]
        [TestCase(4, 1, 4)]
        [TestCase(2, 20, 1)]
        public void ReadAsync_ChunksByMaxKeysPerRead(int keyCount, int maxKeysPerRead, int expectedCalls)
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxKeysPerRead: maxKeysPerRead, maxKeysPerWrite: 1);
            CloudReadRequest[] requests = ReadRequests(keyCount);
            for (int i = 0; i < keyCount; i += 2)
            {
                _provider.SetRawText(AccountId, requests[i].Key, "{\"i\":" + i + "}");
            }

            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(_gateway.ReadAsync(requests, CancellationToken.None));

            Assert.That(_provider.ReadCallCount, Is.EqualTo(expectedCalls));
            Assert.That(_provider.BatchLimitViolationCount, Is.EqualTo(0));
            AssertCallsCoverKeysInOrder(CloudOperation.Read, KeysOf(requests), maxKeysPerRead);
            Assert.That(results.Count, Is.EqualTo(keyCount));
            for (int i = 0; i < keyCount; i++)
            {
                Assert.That(results[i].Key, Is.EqualTo(requests[i].Key), "Results keep request order.");
                Assert.That(results[i].Status, Is.EqualTo(i % 2 == 0 ? CloudReadStatus.Found : CloudReadStatus.NotFound), requests[i].Key);
            }
        }

        [TestCase(5, 2, 3)]
        [TestCase(4, 2, 2)]
        [TestCase(3, 1, 3)]
        [TestCase(2, 20, 1)]
        public void WriteAsync_ChunksByMaxKeysPerWrite(int keyCount, int maxKeysPerWrite, int expectedCalls)
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxKeysPerWrite: maxKeysPerWrite, maxKeysPerRead: 1);
            CloudWriteRequest[] requests = WriteRequests(keyCount);

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(_gateway.WriteAsync(requests, CancellationToken.None));

            Assert.That(_provider.WriteCallCount, Is.EqualTo(expectedCalls));
            Assert.That(_provider.BatchLimitViolationCount, Is.EqualTo(0));
            AssertCallsCoverKeysInOrder(CloudOperation.Write, KeysOf(requests), maxKeysPerWrite);
            Assert.That(results.Count, Is.EqualTo(keyCount));
            for (int i = 0; i < keyCount; i++)
            {
                Assert.That(results[i].Key, Is.EqualTo(requests[i].Key), "Results keep request order.");
                Assert.That(results[i].IsSuccess, Is.True, Describe(results[i]));
                Assert.That(_provider.Store.Contains(AccountId, requests[i].Key), Is.True, requests[i].Key);
            }
        }

        [Test]
        public void EmptyRequests_NoProviderCalls()
        {
            Assert.That(AsyncTestUtility.RunSync(_gateway.ReadAsync(new CloudReadRequest[0], CancellationToken.None)).Count, Is.EqualTo(0));
            Assert.That(AsyncTestUtility.RunSync(_gateway.WriteAsync(new CloudWriteRequest[0], CancellationToken.None)).Count, Is.EqualTo(0));

            Assert.That(_provider.TotalCallCount, Is.EqualTo(0));
        }

        // Size and quota prechecks

        [Test]
        public void WriteAsync_OversizeValue_PayloadTooLarge_WithoutProviderCall()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxValueBytes: 10);

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("big", Payload(11)) }, CancellationToken.None));

            AssertWriteFailed(results[0], CloudErrorKind.PayloadTooLarge);
            Assert.That(_provider.TotalCallCount, Is.EqualTo(0));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void WriteAsync_OversizeValueInBatch_OnlyOtherKeysReachProvider()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxValueBytes: 10);
            CloudWriteRequest[] requests =
            {
                WriteRequest("ok-1", Payload(10)),
                WriteRequest("big", Payload(11)),
                WriteRequest("ok-2", Payload(3)),
            };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(_gateway.WriteAsync(requests, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            AssertWriteFailed(results[1], CloudErrorKind.PayloadTooLarge);
            Assert.That(results[2].IsSuccess, Is.True, Describe(results[2]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
            Assert.That(_provider.Calls[0].Keys, Is.EqualTo(new[] { "ok-1", "ok-2" }));
            Assert.That(_provider.CountCalls(CloudOperation.Write, "big"), Is.EqualTo(0));
        }

        [Test]
        public void WriteAsync_GrowthPastQuota_QuotaExceeded_WithoutProviderCall()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            var stored = new Dictionary<string, long> { { "existing", 60 } };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("new", Payload(41)) }, stored, CancellationToken.None));

            AssertWriteFailed(results[0], CloudErrorKind.QuotaExceeded);
            Assert.That(_provider.TotalCallCount, Is.EqualTo(0));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void WriteAsync_ExactlyAtQuota_IsSent()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            var stored = new Dictionary<string, long> { { "existing", 60 } };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("new", Payload(40)) }, stored, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
        }

        [Test]
        public void WriteAsync_ReplacingStoredKey_CountsOnlyGrowth()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            var stored = new Dictionary<string, long> { { "slot", 60 }, { "other", 30 } };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("slot", Payload(70)) }, stored, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
        }

        [Test]
        public void WriteAsync_ShrinkingKey_AllowedWhenAlreadyOverQuota()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            var stored = new Dictionary<string, long> { { "slot", 150 } };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("slot", Payload(50)) }, stored, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
        }

        [Test]
        public void WriteAsync_QuotaAccumulatesAcrossBatch()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            CloudWriteRequest[] requests =
            {
                WriteRequest("a", Payload(60)),
                WriteRequest("b", Payload(60)),
                WriteRequest("c", Payload(40)),
            };

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(requests, new Dictionary<string, long>(), CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            AssertWriteFailed(results[1], CloudErrorKind.QuotaExceeded);
            Assert.That(results[2].IsSuccess, Is.True, Describe(results[2]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
            Assert.That(_provider.Calls[0].Keys, Is.EqualTo(new[] { "a", "c" }));
        }

        [Test]
        public void WriteAsync_WithoutStoredBytesOrQuota_SkipsQuotaPrecheck()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 100);
            AsyncTestUtility.RunSync(_gateway.WriteAsync(new[] { WriteRequest("a", Payload(150)) }, CancellationToken.None));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1), "Null storedBytesByKey never prechecks the quota.");

            _provider.ClearCalls();
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxTotalBytes: 0);
            var stored = new Dictionary<string, long> { { "huge", long.MaxValue / 2 } };
            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("b", Payload(150)) }, stored, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1), "MaxTotalBytes 0 means unlimited.");
        }

        // NotFound

        [Test]
        public void ReadAsync_NotFound_IsResult_NeverRetried()
        {
            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(_gateway.ReadAsync(new[] { ReadRequest("missing") }, CancellationToken.None));

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.NotFound));
            Assert.That(results[0].Error, Is.Null);
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void DeleteAsync_NotFound_IsResult_NeverRetried()
        {
            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("missing", null, CancellationToken.None));

            Assert.That(result.IsNotFound, Is.True);
            Assert.That(result.Error, Is.Null);
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        // Retryable errors

        [Test]
        public void ReadAsync_Transient_RetriedWithBackoffFromClock()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Read, "k", CloudErrorKind.Transient, 2);

            UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None);

            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1) }));

            _clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1), "No retry before the delay elapses.");

            _clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(2));
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1), Seconds(2) }));
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));

            _clock.Advance(Seconds(2));
            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.Found));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(3));
        }

        [Test]
        public void WriteAsync_Transient_RetriedAfterClockDelay()
        {
            _provider.EnqueueError(CloudOperation.Write, "k", CloudErrorKind.Transient);

            UniTask<IReadOnlyList<CloudWriteResult>> task = _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None);

            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1) }));

            _clock.Advance(Seconds(1));
            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(2));
            Assert.That(_provider.Store.Contains(AccountId, "k"), Is.True);
        }

        [Test]
        public void DeleteAsync_Transient_RetriedAfterClockDelay()
        {
            string version = _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Delete, "k", CloudErrorKind.Transient);

            UniTask<CloudDeleteResult> task = _gateway.DeleteAsync("k", version, CancellationToken.None);

            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(1));
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1) }));

            _clock.Advance(Seconds(1));
            CloudDeleteResult result = AsyncTestUtility.RunSync(task);

            Assert.That(result.IsSuccess, Is.True, result.Error?.ToString());
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(2));
            Assert.That(_provider.Store.Contains(AccountId, "k"), Is.False);
        }

        [Test]
        public void WriteAsync_OnlyFailedKeysAreRetried()
        {
            _provider.EnqueueError(CloudOperation.Write, "a", CloudErrorKind.Transient);
            CloudWriteRequest[] requests = { WriteRequest("a", Payload(4)), WriteRequest("b", Payload(4)) };

            UniTask<IReadOnlyList<CloudWriteResult>> task = _gateway.WriteAsync(requests, CancellationToken.None);
            _clock.Advance(Seconds(1));
            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(results[1].IsSuccess, Is.True, Describe(results[1]));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(2));
            Assert.That(_provider.Calls[1].Keys, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void ReadAsync_NotFoundKeyInRetriedBatch_IsNotReRead()
        {
            _provider.EnqueueError(CloudOperation.Read, "flaky", CloudErrorKind.Transient);

            UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("flaky"), ReadRequest("missing") }, CancellationToken.None);
            _clock.Advance(Seconds(1));
            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.NotFound));
            Assert.That(results[1].Status, Is.EqualTo(CloudReadStatus.NotFound));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(2));
            Assert.That(_provider.CountCalls(CloudOperation.Read, "missing"), Is.EqualTo(1));
        }

        [Test]
        public void ReadAsync_TransientRetriesExhausted_ReturnsLastFailure()
        {
            _gateway = CreateGateway(2);
            _provider.EnqueueError(CloudOperation.Read, "k", CloudErrorKind.Transient, -1);

            UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None);
            _clock.Advance(Seconds(1));
            _clock.Advance(Seconds(2));
            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.Failed));
            Assert.That(results[0].Error.Kind, Is.EqualTo(CloudErrorKind.Transient));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(3), "First attempt plus RetryCount retries.");
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1), Seconds(2) }));
            Assert.That(_clock.PendingDelayCount, Is.EqualTo(0));
        }

        [Test]
        public void WriteAsync_TransientBackoff_CappedAtMaxDelay()
        {
            _gateway = CreateGateway(6);
            _provider.EnqueueError(CloudOperation.Write, "k", CloudErrorKind.Transient, -1);

            UniTask<IReadOnlyList<CloudWriteResult>> task = _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None);
            while (_clock.AdvanceToNextDue())
            {
            }

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(task);

            AssertWriteFailed(results[0], CloudErrorKind.Transient);
            Assert.That(_provider.WriteCallCount, Is.EqualTo(7));
            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1), Seconds(2), Seconds(4), Seconds(8), Seconds(16), Seconds(30) }));
        }

        [Test]
        public void ReadAsync_RateLimited_WaitsForRetryAfter()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Read, "k", CloudErrorKind.RateLimited, 1, Seconds(5));

            UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None);

            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(5) }));
            _clock.Advance(Seconds(4));
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));

            _clock.Advance(Seconds(1));
            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.Found));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(2));
        }

        [Test]
        public void WriteAsync_RateLimitedWithoutRetryAfter_UsesBackoff()
        {
            _provider.EnqueueError(CloudOperation.Write, "k", CloudErrorKind.RateLimited);

            UniTask<IReadOnlyList<CloudWriteResult>> task = _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None);

            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(1) }));
            _clock.Advance(Seconds(1));
            Assert.That(AsyncTestUtility.RunSync(task)[0].IsSuccess, Is.True);
            Assert.That(_provider.WriteCallCount, Is.EqualTo(2));
        }

        [Test]
        public void DeleteAsync_RateLimited_WaitsForRetryAfter()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Delete, "k", CloudErrorKind.RateLimited, 1, Seconds(12));

            UniTask<CloudDeleteResult> task = _gateway.DeleteAsync("k", null, CancellationToken.None);

            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(12) }));
            _clock.Advance(Seconds(12));
            Assert.That(AsyncTestUtility.RunSync(task).IsSuccess, Is.True);
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(2));
        }

        [Test]
        public void RateLimited_RetryAfterAboveMaxDelay_NotRetried()
        {
            _provider.EnqueueError(CloudOperation.Read, "k", CloudErrorKind.RateLimited, -1, Seconds(31));
            _provider.EnqueueError(CloudOperation.Write, "k", CloudErrorKind.RateLimited, -1, Seconds(31));
            _provider.EnqueueError(CloudOperation.Delete, "k", CloudErrorKind.RateLimited, -1, Seconds(31));

            IReadOnlyList<CloudReadResult> read = AsyncTestUtility.RunSync(_gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None));
            IReadOnlyList<CloudWriteResult> write = AsyncTestUtility.RunSync(_gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None));
            CloudDeleteResult delete = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", null, CancellationToken.None));

            Assert.That(read[0].Error.Kind, Is.EqualTo(CloudErrorKind.RateLimited));
            AssertWriteFailed(write[0], CloudErrorKind.RateLimited);
            Assert.That(delete.Error.Kind, Is.EqualTo(CloudErrorKind.RateLimited));
            Assert.That(_provider.TotalCallCount, Is.EqualTo(3));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void ReadAsync_BatchRetry_WaitsForLongestDelay()
        {
            _provider.EnqueueError(CloudOperation.Read, "a", CloudErrorKind.Transient);
            _provider.EnqueueError(CloudOperation.Read, "b", CloudErrorKind.RateLimited, 1, Seconds(7));

            UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("a"), ReadRequest("b") }, CancellationToken.None);

            Assert.That(_clock.RequestedDelays, Is.EqualTo(new[] { Seconds(7) }));
            _clock.Advance(Seconds(7));
            AsyncTestUtility.RunSync(task);

            Assert.That(_provider.ReadCallCount, Is.EqualTo(2));
            Assert.That(_provider.Calls[1].Keys, Is.EqualTo(new[] { "a", "b" }));
        }

        // Non-retryable errors

        [TestCase(CloudErrorKind.Permanent)]
        [TestCase(CloudErrorKind.Unauthorized)]
        [TestCase(CloudErrorKind.Conflict)]
        [TestCase(CloudErrorKind.NotSignedIn)]
        [TestCase(CloudErrorKind.PayloadTooLarge)]
        [TestCase(CloudErrorKind.QuotaExceeded)]
        public void ReadAsync_NonRetryableError_NotRetried(CloudErrorKind kind)
        {
            _provider.EnqueueError(CloudOperation.Read, "k", kind, -1, Seconds(1));

            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(_gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None));

            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.Failed));
            Assert.That(results[0].Error.Kind, Is.EqualTo(kind));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [TestCase(CloudErrorKind.Permanent)]
        [TestCase(CloudErrorKind.Unauthorized)]
        [TestCase(CloudErrorKind.Conflict)]
        [TestCase(CloudErrorKind.NotSignedIn)]
        [TestCase(CloudErrorKind.PayloadTooLarge)]
        [TestCase(CloudErrorKind.QuotaExceeded)]
        public void WriteAsync_NonRetryableError_NotRetried(CloudErrorKind kind)
        {
            _provider.EnqueueError(CloudOperation.Write, "k", kind, -1, Seconds(1));

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(_gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None));

            AssertWriteFailed(results[0], kind);
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [TestCase(CloudErrorKind.Permanent)]
        [TestCase(CloudErrorKind.Unauthorized)]
        [TestCase(CloudErrorKind.Conflict)]
        [TestCase(CloudErrorKind.NotSignedIn)]
        public void DeleteAsync_NonRetryableError_NotRetried(CloudErrorKind kind)
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Delete, "k", kind, -1, Seconds(1));

            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", null, CancellationToken.None));

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error.Kind, Is.EqualTo(kind));
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void ProviderThrows_ClassifiedPermanent_NotRetried()
        {
            _provider.EnqueueThrow(CloudOperation.Read, new InvalidOperationException("Scripted provider bug."), -1);

            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(_gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None));

            Assert.That(results[0].Error.Kind, Is.EqualTo(CloudErrorKind.Permanent));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
            Assert.That(_logger.Contains(TestLogLevel.Error, "ICloudSaveProvider.ReadAsync threw"), Is.True, _logger.Describe());
        }

        // Conditional versions

        [Test]
        public void WriteAsync_ConditionalWriteSupported_PassesExpectedVersion()
        {
            string version = _provider.SetRawText(AccountId, "k", "{}");

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4), version) }, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_provider.GetAllWriteRequests()[0].ExpectedVersion, Is.EqualTo(version));
        }

        [Test]
        public void WriteAsync_ConditionalWriteSupported_StaleVersionConflictNotRetried()
        {
            _provider.SetRawText(AccountId, "k", "{}");

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4), "stale") }, CancellationToken.None));

            AssertWriteFailed(results[0], CloudErrorKind.Conflict);
            Assert.That(_provider.GetAllWriteRequests()[0].ExpectedVersion, Is.EqualTo("stale"));
            Assert.That(_provider.WriteCallCount, Is.EqualTo(1));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0));
        }

        [Test]
        public void WriteAsync_ConditionalWriteUnsupported_DropsExpectedVersion()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(supportsConditionalWrite: false);
            _provider.SetRawText(AccountId, "k", "{}");
            byte[] value = Payload(4);

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                _gateway.WriteAsync(new[] { new CloudWriteRequest("k", value, "stale", CloudAccess.ClientOwned) }, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            CloudWriteRequest sent = _provider.GetAllWriteRequests()[0];
            Assert.That(sent.ExpectedVersion, Is.Null);
            Assert.That(sent.Key, Is.EqualTo("k"));
            Assert.That(sent.Value, Is.EqualTo(value));
            Assert.That(sent.Access, Is.EqualTo(CloudAccess.ClientOwned));
        }

        [Test]
        public void DeleteAsync_ConditionalWriteSupported_PassesExpectedVersion()
        {
            string version = _provider.SetRawText(AccountId, "k", "{}");

            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", version, CancellationToken.None));

            Assert.That(result.IsSuccess, Is.True, result.Error?.ToString());
            Assert.That(_provider.Calls[0].DeleteExpectedVersion, Is.EqualTo(version));
        }

        [Test]
        public void DeleteAsync_ConditionalWriteUnsupported_DropsExpectedVersion()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(supportsConditionalWrite: false);
            _provider.SetRawText(AccountId, "k", "{}");

            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", "stale", CancellationToken.None));

            Assert.That(result.IsSuccess, Is.True, result.Error?.ToString());
            Assert.That(_provider.Calls[0].DeleteExpectedVersion, Is.Null);
        }

        // Cancellation

        [Test]
        public void ReadAsync_CancelledDuringRetryDelay_StopsRetrying()
        {
            _provider.EnqueueError(CloudOperation.Read, "k", CloudErrorKind.Transient, -1);
            using (var cts = new CancellationTokenSource())
            {
                UniTask<IReadOnlyList<CloudReadResult>> task = _gateway.ReadAsync(new[] { ReadRequest("k") }, cts.Token);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));

                cts.Cancel();

                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Canceled));
                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(task));
                _clock.Advance(TimeSpan.FromMinutes(5));
                Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
                Assert.That(_clock.PendingDelayCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void WriteAsync_CancelledDuringRetryDelay_StopsRetrying()
        {
            _provider.EnqueueError(CloudOperation.Write, "k", CloudErrorKind.Transient, -1);
            using (var cts = new CancellationTokenSource())
            {
                UniTask<IReadOnlyList<CloudWriteResult>> task = _gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, cts.Token);
                _clock.Advance(Seconds(1));
                Assert.That(_provider.WriteCallCount, Is.EqualTo(2));
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));

                cts.Cancel();

                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Canceled));
                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(task));
                _clock.Advance(TimeSpan.FromMinutes(5));
                Assert.That(_provider.WriteCallCount, Is.EqualTo(2));
                Assert.That(_provider.Store.Contains(AccountId, "k"), Is.False);
            }
        }

        [Test]
        public void DeleteAsync_CancelledDuringRetryDelay_StopsRetrying()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Delete, "k", CloudErrorKind.Transient, -1);
            using (var cts = new CancellationTokenSource())
            {
                UniTask<CloudDeleteResult> task = _gateway.DeleteAsync("k", null, cts.Token);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));

                cts.Cancel();

                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Canceled));
                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(task));
                _clock.Advance(TimeSpan.FromMinutes(5));
                Assert.That(_provider.DeleteCallCount, Is.EqualTo(1));
                Assert.That(_provider.Store.Contains(AccountId, "k"), Is.True);
            }
        }

        [Test]
        public void PreCancelledToken_ThrowsWithoutProviderCalls()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(_gateway.ReadAsync(new[] { ReadRequest("k") }, cts.Token)));
                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(_gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, cts.Token)));
                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", null, cts.Token)));

                Assert.That(_provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void ReadAsync_CancelledBetweenChunks_SkipsRemainingChunks()
        {
            _provider.Capabilities = FakeCloudSaveProvider.CreateCapabilities(maxKeysPerRead: 2);
            using (var cts = new CancellationTokenSource())
            {
                _provider.AfterRead = (provider, requests) => cts.Cancel();

                Assert.Catch<OperationCanceledException>(() => AsyncTestUtility.RunSync(_gateway.ReadAsync(ReadRequests(5), cts.Token)));

                Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            }
        }

        // Call timeout (S3)

        [Test]
        public void ReadAsync_ProviderNeverReturns_TimesOutTransientPerKey()
        {
            CloudGateway gateway = CreateGateway(0, CallTimeout);
            _provider.IgnoresCancellationWhileHeld = true;
            _provider.HoldReads();
            try
            {
                UniTask<IReadOnlyList<CloudReadResult>> task = gateway.ReadAsync(ReadRequests(2), CancellationToken.None);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending), "Premise: the wedged call holds the gateway.");

                _clock.Advance(CallTimeout);

                IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);
                Assert.That(results.Count, Is.EqualTo(2));
                for (int i = 0; i < results.Count; i++)
                {
                    Assert.That(results[i].Status, Is.EqualTo(CloudReadStatus.Failed), results[i].Key);
                    Assert.That(results[i].Error.Kind, Is.EqualTo(CloudErrorKind.Transient), results[i].Error.ToString());
                }

                Assert.That(_provider.ReadCallCount, Is.EqualTo(1), "Without retries the abandoned call is not repeated.");
                Assert.That(_provider.HeldCallCount, Is.EqualTo(1), "Premise: the abandoned call is still wedged.");
                Assert.That(_clock.PendingDelayCount, Is.EqualTo(0), "The timeout timer is cancelled.");
                Assert.That(_logger.Contains(TestLogLevel.Error, "ReadAsync did not complete"), Is.True, _logger.Describe());
            }
            finally
            {
                _provider.ReleaseReads();
            }
        }

        [Test]
        public void DeleteAsync_ProviderNeverReturns_TimesOutTransient_AndRetriesOnce()
        {
            CloudGateway gateway = CreateGateway(1, CallTimeout);
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.IgnoresCancellationWhileHeld = true;
            _provider.HoldDeletes();
            try
            {
                UniTask<CloudDeleteResult> task = gateway.DeleteAsync("k", null, CancellationToken.None);

                // The first attempt is abandoned; the transient classification schedules the retry
                _clock.Advance(CallTimeout);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));
                Assert.That(_provider.DeleteCallCount, Is.EqualTo(1));

                // Backoff, then the retry hangs as well
                _clock.Advance(BaseDelay);
                Assert.That(_provider.DeleteCallCount, Is.EqualTo(2), "The retry reaches the provider.");
                _clock.Advance(CallTimeout);

                CloudDeleteResult result = AsyncTestUtility.RunSync(task);
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.IsNotFound, Is.False);
                Assert.That(result.Error.Kind, Is.EqualTo(CloudErrorKind.Transient), result.Error.ToString());
                Assert.That(_provider.DeleteCallCount, Is.EqualTo(2), "Retries stop when they run out.");
                Assert.That(_clock.PendingDelayCount, Is.EqualTo(0));
                Assert.That(_provider.Store.Contains(AccountId, "k"), Is.True, "Nothing was deleted.");
            }
            finally
            {
                _provider.ReleaseDeletes();
            }
        }

        [Test]
        public void ReadAsync_ProviderAnswersBeforeTimeout_CancelsTheTimeoutTimer()
        {
            CloudGateway gateway = CreateGateway(0, CallTimeout);
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.HoldReads();

            UniTask<IReadOnlyList<CloudReadResult>> task = gateway.ReadAsync(new[] { ReadRequest("k") }, CancellationToken.None);
            Assert.That(_clock.PendingDelayCount, Is.EqualTo(1), "Premise: an asynchronous call arms the timer.");

            _provider.ReleaseReads();

            IReadOnlyList<CloudReadResult> results = AsyncTestUtility.RunSync(task);
            Assert.That(results[0].Status, Is.EqualTo(CloudReadStatus.Found), results[0].Error?.ToString());
            Assert.That(_clock.PendingDelayCount, Is.EqualTo(0), "The timer is cancelled once the call answers.");

            // The cancelled timer must not fire afterwards
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.That(_provider.ReadCallCount, Is.EqualTo(1));
            Assert.That(_logger.Count(TestLogLevel.Error), Is.EqualTo(0), _logger.Describe());
        }

        [Test]
        public void WriteAsync_ProviderAnswersSynchronously_NoTimerIsArmed()
        {
            CloudGateway gateway = CreateGateway(0, CallTimeout);

            IReadOnlyList<CloudWriteResult> results = AsyncTestUtility.RunSync(
                gateway.WriteAsync(new[] { WriteRequest("k", Payload(4)) }, CancellationToken.None));

            Assert.That(results[0].IsSuccess, Is.True, Describe(results[0]));
            Assert.That(_clock.DelayCallCount, Is.EqualTo(0), "A provider that answers synchronously needs no timer.");
        }

        // Caller guard (D4)

        [Test]
        public void DeleteAsync_GuardRefusesFirstAttempt_NoProviderCall()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            var guard = new CountingGuard(0);

            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", null, guard, CancellationToken.None));

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error.Message, Is.EqualTo(CountingGuard.RefusalMessage), result.Error.ToString());
            Assert.That(guard.CheckCount, Is.EqualTo(1));
            Assert.That(_provider.TotalCallCount, Is.EqualTo(0));
            Assert.That(_provider.Store.Contains(AccountId, "k"), Is.True);
        }

        // The guard runs inside the retry loop, so a precondition that lapsed during the backoff stops the retry
        [Test]
        public void DeleteAsync_GuardRefusesBeforeRetry_SecondAttemptNeverReachesProvider()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            _provider.EnqueueError(CloudOperation.Delete, "k", CloudErrorKind.Transient, -1);
            var guard = new CountingGuard(1);

            UniTask<CloudDeleteResult> task = _gateway.DeleteAsync("k", null, guard, CancellationToken.None);
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending), "Premise: the transient failure waits for its backoff.");

            _clock.Advance(BaseDelay);

            CloudDeleteResult result = AsyncTestUtility.RunSync(task);
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error.Message, Is.EqualTo(CountingGuard.RefusalMessage), result.Error.ToString());
            Assert.That(guard.CheckCount, Is.EqualTo(2));
            Assert.That(_provider.DeleteCallCount, Is.EqualTo(1), "The retry is refused before it reaches the provider.");
            Assert.That(_provider.Store.Contains(AccountId, "k"), Is.True);
        }

        [Test]
        public void DeleteAsync_GuardAllowsEveryAttempt_Deletes()
        {
            _provider.SetRawText(AccountId, "k", "{}");
            var guard = new CountingGuard(-1);

            CloudDeleteResult result = AsyncTestUtility.RunSync(_gateway.DeleteAsync("k", null, guard, CancellationToken.None));

            Assert.That(result.IsSuccess, Is.True, result.Error?.ToString());
            Assert.That(guard.CheckCount, Is.EqualTo(1));
            Assert.That(_provider.Store.Contains(AccountId, "k"), Is.False);
        }

        private CloudGateway CreateGateway(int retryCount)
        {
            return new CloudGateway(_provider, new RetryPolicy(retryCount, BaseDelay, MaxDelay, _clock), _logger);
        }

        private CloudGateway CreateGateway(int retryCount, TimeSpan callTimeout)
        {
            return new CloudGateway(_provider, new RetryPolicy(retryCount, BaseDelay, MaxDelay, _clock), _logger, callTimeout);
        }

        private void AssertCallsCoverKeysInOrder(CloudOperation operation, string[] expectedKeys, int limit)
        {
            var sentKeys = new List<string>();
            foreach (CloudCall call in _provider.Calls)
            {
                if (call.Operation != operation)
                {
                    continue;
                }

                Assert.That(call.Keys.Count, Is.InRange(1, limit), call.ToString());
                sentKeys.AddRange(call.Keys);
            }

            Assert.That(sentKeys, Is.EqualTo(expectedKeys));
        }

        private static void AssertWriteFailed(CloudWriteResult result, CloudErrorKind kind)
        {
            Assert.That(result.IsSuccess, Is.False, result.Key);
            Assert.That(result.Error, Is.Not.Null, result.Key);
            Assert.That(result.Error.Kind, Is.EqualTo(kind), result.Error.ToString());
        }

        private static string Describe(CloudWriteResult result)
        {
            return result.Key + ": " + (result.IsSuccess ? "success" : result.Error?.ToString());
        }

        private static CloudReadRequest ReadRequest(string key)
        {
            return new CloudReadRequest(key, CloudAccess.ClientOwned);
        }

        private static CloudReadRequest[] ReadRequests(int count)
        {
            var requests = new CloudReadRequest[count];
            for (int i = 0; i < count; i++)
            {
                requests[i] = ReadRequest("key-" + i);
            }

            return requests;
        }

        private static CloudWriteRequest WriteRequest(string key, byte[] value, string expectedVersion = null)
        {
            return new CloudWriteRequest(key, value, expectedVersion, CloudAccess.ClientOwned);
        }

        private static CloudWriteRequest[] WriteRequests(int count)
        {
            var requests = new CloudWriteRequest[count];
            for (int i = 0; i < count; i++)
            {
                requests[i] = WriteRequest("key-" + i, Encoding.UTF8.GetBytes("{\"i\":" + i + "}"));
            }

            return requests;
        }

        private static string[] KeysOf(CloudReadRequest[] requests)
        {
            return Array.ConvertAll(requests, request => request.Key);
        }

        private static string[] KeysOf(CloudWriteRequest[] requests)
        {
            return Array.ConvertAll(requests, request => request.Key);
        }

        // Content is irrelevant to the gateway; only the length matters
        private static byte[] Payload(int size)
        {
            var bytes = new byte[size];
            for (int i = 0; i < size; i++)
            {
                bytes[i] = (byte)'a';
            }

            return bytes;
        }

        private static TimeSpan Seconds(int seconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>Allows the first allowedAttempts checks and refuses afterwards; a negative count allows every attempt.</summary>
        private sealed class CountingGuard : ICloudCallGuard
        {
            public const string RefusalMessage = "Refused by the caller guard.";

            private readonly int _allowedAttempts;

            public CountingGuard(int allowedAttempts)
            {
                _allowedAttempts = allowedAttempts;
            }

            public int CheckCount { get; private set; }

            public CloudError CheckBeforeAttempt()
            {
                CheckCount++;
                return _allowedAttempts < 0 || CheckCount <= _allowedAttempts
                    ? null
                    : new CloudError(CloudErrorKind.Transient, RefusalMessage);
            }
        }
    }
}
