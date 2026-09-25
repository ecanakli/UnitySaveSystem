using System.Collections.Generic;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>F6 detector: changes made outside Mutate are reported on flush, once, and never persisted by the flush.</summary>
    [TestFixture]
    public sealed class MutationDetectorTests
    {
        private const string DetectionMessage = "changed outside Mutate";

        private static readonly string PlayerPath = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);

        [Test]
        public void MutationThroughRetainedReadReference_DetectedOnFlush_LoggedOnce_NotPersisted()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(true, slot))
            {
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                long revision = slot.Revision;
                context.Storage.ResetCounters();

                // Anti-pattern under test: keeping the live list past Read
                List<string> retained = slot.Read(data => data.Items);
                retained.Add("smuggled");

                LocalFlushResult flushed = context.Service.FlushLocalNow();

                Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                Assert.That(context.Logger.Count(TestLogLevel.Error, DetectionMessage), Is.EqualTo(1), context.Logger.Describe());
                Assert.That(context.Logger.Contains(TestLogLevel.Error, "'" + TestSlotKeys.Player + "'"), Is.True, context.Logger.Describe());
                Assert.That(context.Storage.GetWriteCount(PlayerPath), Is.EqualTo(0), "Detection does not save the change.");
                Assert.That(context.Storage.GetText(PlayerPath), Does.Not.Contain("smuggled"));
                Assert.That(slot.Revision, Is.EqualTo(revision), "Detection does not bump the revision.");

                // Further outside changes in the same epoch stay silent
                retained.Add("again");
                context.Service.FlushLocalNow();
                context.Service.FlushLocalNow();

                Assert.That(context.Logger.Count(TestLogLevel.Error, DetectionMessage), Is.EqualTo(1), context.Logger.Describe());
                Assert.That(context.Storage.GetWriteCount(PlayerPath), Is.EqualTo(0));
            }
        }

        [Test]
        public void MutateCalls_NoFalsePositive()
        {
            var player = new ProfileSlot();
            var settings = new DeviceSettingsSlot();
            using (TestServiceContext context = CreateInitializedGuest(true, player, settings))
            {
                // Clean flush right after load checks the load baseline
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(player.Mutate(data => data.Items.Add("sword")), Is.True);
                Assert.That(settings.Mutate(data => data.MusicVolume = 0.5f), Is.True);
                Assert.That(player.Read(data => data.Items.Count), Is.EqualTo(1));
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                // Same value written again, then a real change
                Assert.That(player.Mutate(data => data.Coins = 0), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(player.Mutate(data => data.Level = 4), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                // Profile switch reloads and captures new baselines
                Assert.That(context.ActivateSync(ProfileId.Local("campaign")).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 2), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(player.Read(data => data.Items.Count), Is.EqualTo(1), "Premise: the guest data was reloaded.");

                Assert.That(context.Logger.Count(TestLogLevel.Error, DetectionMessage), Is.EqualTo(0), context.Logger.Describe());
            }
        }

        [Test]
        public void DetectorDisabled_LogsNothing_NothingWritten()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(false, slot))
            {
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                context.Storage.ResetCounters();

                List<string> retained = slot.Read(data => data.Items);
                retained.Add("smuggled");
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(context.Logger.Count(TestLogLevel.Error, DetectionMessage), Is.EqualTo(0), context.Logger.Describe());
                Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
                Assert.That(context.Storage.GetWriteCount(PlayerPath), Is.EqualTo(0));
            }
        }

        // Guest profile with the detector switched explicitly; counters and log cleared after init
        private static TestServiceContext CreateInitializedGuest(bool detect, params SaveSlot[] slots)
        {
            TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options =>
                {
                    options.CloudRetryCount = 0;
                    options.DetectMutationsOutsideMutate = detect;
                },
            });

            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            context.Storage.ResetCounters();
            context.Logger.Clear();
            return context;
        }
    }
}
