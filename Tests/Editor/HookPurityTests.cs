using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Hooks must stay pure: Mutate, SaveNowAsync and service calls from a hook are refused and logged; the scope flag always clears.</summary>
    [TestFixture]
    public sealed class HookPurityTests
    {
        private const string RefusedFrom = " was refused because it was called from ";

        [TearDown]
        public void TearDown()
        {
            Assert.That(HookScope.IsActive, Is.False, "A test left the hook scope active.");
        }

        [Test]
        public void Normalize_CallingMutateOnAnotherSlot_IsRefusedAndLogsError()
        {
            var other = new ProfileSlot();
            var hookSlot = new HookCallsServiceSlot(syncMode: SyncMode.LocalOnly);
            var results = new List<bool>();
            hookSlot.NormalizeAction = _ => results.Add(other.Mutate(data => data.Coins = 99));

            using (TestServiceContext context = TestServiceFactory.Create(other, hookSlot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);

                Assert.That(results, Is.Not.Empty, "Normalize ran during the load.");
                Assert.That(results, Has.All.False);
                Assert.That(other.Read(data => data.Coins), Is.EqualTo(0));
                Assert.That(other.Revision, Is.EqualTo(0));
                Assert.That(
                    context.Logger.Contains(TestLogLevel.Error, "Mutate on slot 'player'" + RefusedFrom + "Normalize of slot 'hook_calls'"),
                    Is.True,
                    context.Logger.Describe());

                // Outside the hook the same call is accepted
                Assert.That(other.Mutate(data => data.Coins = 1), Is.True);
            }
        }

        [Test]
        public void UpgradePayload_CallingSaveService_IsRefusedAndLogsError()
        {
            var hookSlot = new HookCallsServiceSlot(syncMode: SyncMode.LocalOnly, schemaVersion: 2);
            var storage = new InMemorySaveStorage();
            string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.HookCalls);
            byte[] v1 = EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1, Revision = 4, Data = new JObject { { "Coins", 7 } } }).Bytes;
            storage.SetBytes(path, v1);

            using (TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup { Slots = new SaveSlot[] { hookSlot }, Storage = storage }))
            {
                LocalFlushResult flushFromHook = null;
                bool? readyFromHook = null;
                hookSlot.Service = context.Service;
                hookSlot.UpgradeFunc = (payload, from) =>
                {
                    flushFromHook = context.Service.FlushLocalNow();
                    readyFromHook = AsyncTestUtility.RunSync(context.Service.WhenReadyAsync(CancellationToken.None));
                    return payload;
                };

                Assert.That(context.InitializeSync().IsSuccess, Is.True);

                Assert.That(hookSlot.UpgradeFromVersions, Is.EqualTo(new[] { 1 }));
                Assert.That(flushFromHook, Is.Not.Null);
                Assert.That(flushFromHook.IsComplete, Is.False);
                Assert.That(readyFromHook, Is.EqualTo(false));
                Assert.That(context.Logger.Contains(TestLogLevel.Error, "FlushLocalNow" + RefusedFrom + "UpgradePayload of slot 'hook_calls'"), Is.True, context.Logger.Describe());
                Assert.That(context.Logger.Contains(TestLogLevel.Error, "WhenReadyAsync" + RefusedFrom + "UpgradePayload of slot 'hook_calls'"), Is.True, context.Logger.Describe());
                Assert.That(hookSlot.State, Is.EqualTo(SlotState.Ready));
                Assert.That(hookSlot.Read(data => data.Coins), Is.EqualTo(7));
            }
        }

        [Test]
        public void ResolveConflict_CallingSaveNowAsync_IsRefused()
        {
            var hookSlot = new HookCallsServiceSlot();
            using (TestServiceContext context = TestServiceFactory.Create(hookSlot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                SaveResult fromHook = default;
                bool called = false;
                hookSlot.ConflictResolver = (in ConflictContext<ProfileData> conflict) =>
                {
                    fromHook = AsyncTestUtility.RunSync(hookSlot.SaveNowAsync(CancellationToken.None));
                    called = true;
                    return ConflictResolution<ProfileData>.KeepLocal;
                };
                var metadata = new ConflictMetadata(0, 1, null, "device-b", false);

                SlotConflictDecision decision = hookSlot.ResolveConflictBoxed(new ProfileData { Coins = 1 }, new ProfileData { Coins = 2 }, in metadata);

                Assert.That(called, Is.True);
                Assert.That(decision.IsSuccess, Is.True);
                Assert.That(decision.Kind, Is.EqualTo(ConflictResolutionKind.KeepLocal));
                Assert.That(fromHook.IsSuccess, Is.False);
                Assert.That(fromHook.Error.Code, Is.EqualTo(SaveErrorCode.CalledFromHook));
                Assert.That(
                    context.Logger.Contains(TestLogLevel.Error, "SaveNowAsync on slot 'hook_calls'" + RefusedFrom + "ResolveConflict of slot 'hook_calls'"),
                    Is.True,
                    context.Logger.Describe());
                Assert.That(context.Storage.CountCalls(StorageOperation.Write, TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.HookCalls)), Is.EqualTo(0));
            }
        }

        [Test]
        public void IsEmpty_CallingMutate_IsRefusedAndLogsError()
        {
            var slot = new ProfileSlot();
            var other = new ProfileSlot("other", SyncMode.LocalOnly);
            using (TestServiceContext context = TestServiceFactory.Create(slot, other))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                bool? mutated = null;
                slot.IsEmptyFunc = data =>
                {
                    mutated = other.Mutate(target => target.Coins = 7);
                    return false;
                };

                Assert.That(slot.EvaluateIsEmpty(slot.PeekData()), Is.False);

                Assert.That(mutated, Is.EqualTo(false));
                Assert.That(other.Read(data => data.Coins), Is.EqualTo(0));
                Assert.That(context.Logger.Contains(TestLogLevel.Error, "Mutate on slot 'other'" + RefusedFrom + "IsEmpty of slot 'player'"), Is.True, context.Logger.Describe());
            }
        }

        [Test]
        public void HookScopeFlag_ClearedAfterHookThrows_DuringInitialize()
        {
            var other = new ProfileSlot();
            var throwing = new NormalizeThrowsSlot();
            using (TestServiceContext context = TestServiceFactory.Create(other, throwing))
            {
                context.InitializeSync();

                Assert.That(throwing.State, Is.EqualTo(SlotState.Failed).Or.EqualTo(SlotState.Ready));
                Assert.That(HookScope.IsActive, Is.False);
                Assert.That(HookScope.ActiveHookDescription, Is.Null);
                Assert.That(other.Mutate(data => data.Coins = 3), Is.True, context.Logger.Describe());
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Logger.Count(TestLogLevel.Error, RefusedFrom), Is.EqualTo(0), context.Logger.Describe());
            }
        }

        [TestCase("Normalize")]
        [TestCase("CreateDefault")]
        [TestCase("UpgradePayload")]
        [TestCase("IsEmpty")]
        [TestCase("ResolveConflict")]
        public void HookScopeFlag_ClearedAfterHookThrows(string hook)
        {
            var slot = new ProfileSlot();
            var failure = new InvalidOperationException("Scripted " + hook + " failure.");
            switch (hook)
            {
                case "Normalize":
                    slot.NormalizeAction = _ => throw failure;
                    Assert.That(slot.MaterializeNormalized(new JObject()).FailedStage, Is.EqualTo(SlotMaterializeStage.Normalize));
                    break;
                case "CreateDefault":
                    slot.CreateDefaultFactory = () => throw failure;
                    Assert.That(slot.CreateNormalizedDefault().FailedStage, Is.EqualTo(SlotMaterializeStage.CreateDefault));
                    break;
                case "UpgradePayload":
                    slot.UpgradeFunc = (payload, from) => throw failure;
                    Assert.That(slot.RunUpgrade(new JObject(), 0).IsSuccess, Is.False);
                    break;
                case "IsEmpty":
                    slot.IsEmptyFunc = _ => throw failure;
                    Assert.That(slot.EvaluateIsEmpty(new ProfileData()), Is.False);
                    break;
                default:
                    slot.ConflictResolver = (in ConflictContext<ProfileData> conflict) => throw failure;
                    var metadata = new ConflictMetadata(1, 2, null, null, true);
                    Assert.That(slot.ResolveConflictBoxed(new ProfileData(), new ProfileData(), in metadata).IsSuccess, Is.False);
                    break;
            }

            Assert.That(HookScope.IsActive, Is.False);
            Assert.That(HookScope.ActiveHookDescription, Is.Null);
        }

        [Test]
        public void HookScope_NestedThrow_RestoresOuterDescription_ThenClears()
        {
            using (HookScope.Enter("outer", "UpgradePayload"))
            {
                try
                {
                    using (HookScope.Enter("inner", "Normalize"))
                    {
                        Assert.That(HookScope.ActiveHookDescription, Is.EqualTo("Normalize of slot 'inner'"));
                        throw new InvalidOperationException("Scripted inner failure.");
                    }
                }
                catch (InvalidOperationException)
                {
                    // Expected
                }

                Assert.That(HookScope.IsActive, Is.True);
                Assert.That(HookScope.ActiveHookDescription, Is.EqualTo("UpgradePayload of slot 'outer'"));
                Assert.That(HookScope.RefuseIfActive(null, "Probe"), Is.True);
            }

            Assert.That(HookScope.IsActive, Is.False);
            Assert.That(HookScope.ActiveHookDescription, Is.Null);
            Assert.That(HookScope.RefuseIfActive(null, "Probe"), Is.False);
        }
    }
}
