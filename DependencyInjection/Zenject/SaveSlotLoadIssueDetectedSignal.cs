using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.SlotLoadIssueDetected.</summary>
    public sealed class SaveSlotLoadIssueDetectedSignal
    {
        public SaveSlotLoadIssueDetectedSignal(SlotLoadIssue issue)
        {
            Issue = issue;
        }

        public SlotLoadIssue Issue { get; }
    }
}
