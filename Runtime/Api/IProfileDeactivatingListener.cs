using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Awaited before a profile switch so services can write final state into the outgoing profile.</summary>
    public interface IProfileDeactivatingListener
    {
        /// <summary>Ascending dispatch order; ties resolve by registration order.</summary>
        int Order { get; }

        /// <summary>Slots are still Ready; Mutate, SaveNowAsync and FlushAsync are allowed.</summary>
        UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct);
    }

    /// <summary>Profiles involved in a pending switch.</summary>
    public readonly struct ProfileDeactivation
    {
        internal ProfileDeactivation(ProfileId outgoing, ProfileId incoming)
        {
            Outgoing = outgoing;
            Incoming = incoming;
        }

        public ProfileId Outgoing { get; }

        public ProfileId Incoming { get; }

        public override string ToString()
        {
            return Outgoing + " -> " + Incoming;
        }
    }
}
