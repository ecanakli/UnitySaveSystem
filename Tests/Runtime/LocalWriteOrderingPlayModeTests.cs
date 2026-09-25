using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// B1 on a real player loop with OffloadIo = true: two writes for one slot complete out of order and the older one
    /// never replaces the newer one on disk. The offloaded SaveNowAsync write is held on its way to the file while the
    /// upload path writes the next revision of the same slot. That path takes no service write lock, only the store's
    /// per-path gate, which is what orders the two writes and refuses the older one.
    /// </summary>
    [TestFixture]
    public sealed class LocalWriteOrderingPlayModeTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 600;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);
        private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(100);

        /// <summary>Long enough for the cloud lane to dispatch its upload while the first write is still parked.</summary>
        private static readonly TimeSpan ParkDuration = TimeSpan.FromSeconds(2);

        [UnityTest]
        public IEnumerator OutOfOrderLocalWrites_DiskKeepsTheHighestRevision()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    await ActivateReconciledAccountAsync(context, AccountA);
                    string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);

                    Assert.That(slot.Mutate(data => data.Coins = 10), Is.True);
                    long staleRevision = slot.Revision;

                    // The offloaded write is held before it touches the file, so the upload path may overtake it
                    context.Storage.ParkWrites(path, ParkDuration);
                    UniTask<SaveResult> stale = slot.SaveNowAsync(CancellationToken.None).Preserve();
                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.IsWriteParked, MaxFrames, "Parked local write");

                    Assert.That(slot.Mutate(data => data.Coins = 11), Is.True);
                    long newerRevision = slot.Revision;
                    Assert.That(newerRevision, Is.GreaterThan(staleRevision), "Premise: the second write carries a newer revision.");

                    // Disk-first write of the scheduled upload; it never takes the service write lock
                    context.Clock.Advance(context.Options.CloudDebounce + Margin);
                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(path) >= 2, MaxFrames, "Both local writes");

                    SaveResult staleResult = await stale;
                    await AsyncTestUtility.WaitFramesAsync(5);

                    Assert.That(context.Storage.ParkedWriteCount, Is.EqualTo(1), "Premise: exactly one write was held on its way to disk.");
                    long highestDurable = Math.Max(staleResult.IsSuccess ? staleResult.DurableRevision : 0, newerRevision);
                    Assert.That(highestDurable, Is.EqualTo(newerRevision));
                    Assert.That(OnDiskRevision(context, path), Is.EqualTo(highestDurable), "A stale write must never land on top of a newer one.");

                    // Nothing may be left clean over stale bytes: a final flush must not have to repair the file
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(OnDiskRevision(context, path), Is.EqualTo(slot.Revision));
                    Assert.That(ReadCoins(context, path), Is.EqualTo(11));

                    // The guard refuses older revisions only: the next save still lands
                    Assert.That(slot.Mutate(data => data.Coins = 12), Is.True);
                    SaveResult saved = await slot.SaveNowAsync(CancellationToken.None);

                    Assert.That(saved.IsSuccess, Is.True, saved.ToString());
                    Assert.That(saved.DurableRevision, Is.EqualTo(slot.Revision));
                    Assert.That(OnDiskRevision(context, path), Is.EqualTo(slot.Revision));
                    Assert.That(ReadCoins(context, path), Is.EqualTo(12));
                }
            });
        }

        private static TestServiceContext CreateContext(SaveSlot slot)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = new[] { slot },
                ConfigureOptions = options =>
                {
                    options.OffloadIo = true;
                    options.CloudRetryCount = 0;

                    // Only SaveNowAsync and the upload path write during the test
                    options.LocalWriteDelay = TimeSpan.FromSeconds(30);
                },
            });
        }

        // Signed in, active and reconciled this epoch; every step is awaited because OffloadIo needs player-loop ticks
        private static async UniTask ActivateReconciledAccountAsync(TestServiceContext context, string accountId)
        {
            context.Provider.SignedInAccountId = accountId;
            InitializeResult initialized = await context.Service.InitializeAsync(CancellationToken.None);
            Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());

            ProfileActivationResult activated = await context.Service.ActivateProfileAsync(ProfileId.Account(accountId), CancellationToken.None);
            Assert.That(activated.IsSuccess, Is.True, activated.ToString());

            RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());

            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        private static long OnDiskRevision(TestServiceContext context, string path)
        {
            return DecodePrimary(context, path).Revision;
        }

        private static int ReadCoins(TestServiceContext context, string path)
        {
            var data = (JObject)DecodePrimary(context, path).Data;
            Assert.That(data, Is.Not.Null, "The envelope carries no data object.");
            return data.Value<int>("Coins");
        }

        private static SaveEnvelope DecodePrimary(TestServiceContext context, string path)
        {
            byte[] bytes = context.Storage.GetBytes(path);
            Assert.That(bytes, Is.Not.Null, "No file at " + path + ".");
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);
            Assert.That(decoded.IsOk, Is.True, decoded.Message);
            return decoded.Envelope;
        }
    }
}
