using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Choice made by a conflict resolution.</summary>
    public enum ConflictResolutionKind
    {
        KeepLocal = 0,
        TakeCloud = 1,
        Merged = 2,
    }

    /// <summary>Result of SaveSlot.ResolveConflict: KeepLocal, TakeCloud or Merged(data).</summary>
    public readonly struct ConflictResolution<TData> where TData : class
    {
        private ConflictResolution(ConflictResolutionKind kind, TData mergedData)
        {
            Kind = kind;
            MergedData = mergedData;
        }

        public static ConflictResolution<TData> KeepLocal => new ConflictResolution<TData>(ConflictResolutionKind.KeepLocal, null);

        public static ConflictResolution<TData> TakeCloud => new ConflictResolution<TData>(ConflictResolutionKind.TakeCloud, null);

        public ConflictResolutionKind Kind { get; }

        /// <summary>Set only when Kind is Merged.</summary>
        public TData MergedData { get; }

        /// <summary>Uses a new instance built from both sides; never mutate the context inputs.</summary>
        public static ConflictResolution<TData> Merged(TData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return new ConflictResolution<TData>(ConflictResolutionKind.Merged, data);
        }

        public override string ToString()
        {
            return Kind.ToString();
        }
    }
}
