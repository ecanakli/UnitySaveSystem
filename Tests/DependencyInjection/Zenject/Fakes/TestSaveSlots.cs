using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    /// <summary>Minimal payload shared by the test slots.</summary>
    internal sealed class TestSlotData
    {
        public int Value;
    }

    /// <summary>LocalOnly test slot used to prove BindSaveSlot registers into ResolveAll&lt;SaveSlot&gt;().</summary>
    internal sealed class TestSlotA : SaveSlot<TestSlotData>
    {
        public override string Key => "test-slot-a";
    }

    /// <summary>Second LocalOnly test slot, distinct key, used for the "N slots" case.</summary>
    internal sealed class TestSlotB : SaveSlot<TestSlotData>
    {
        public override string Key => "test-slot-b";
    }

    /// <summary>CloudSync test slot used to observe the NullCloudSaveProvider fallback through RestoreAsync.</summary>
    internal sealed class TestCloudSlot : SaveSlot<TestSlotData>
    {
        public override string Key => "test-cloud-slot";

        public override SyncMode SyncMode => SyncMode.CloudSync;
    }

    /// <summary>Slot that also implements IRestoreListener, used to prove BindSaveSlot's additionalContracts share one instance.</summary>
    internal sealed class TestListenerSlot : SaveSlot<TestSlotData>, IRestoreListener
    {
        public override string Key => "test-listener-slot";

        public int Order => 0;

        public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct) => UniTask.CompletedTask;
    }
}
