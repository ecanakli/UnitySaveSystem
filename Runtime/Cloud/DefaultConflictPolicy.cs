namespace Ecanakli.SaveSystem
{
    /// <summary>Default conflict rules: the empty side loses, otherwise cloud wins.</summary>
    public static class DefaultConflictPolicy
    {
        public static ConflictResolution<TData> Resolve<TData>(in ConflictContext<TData> context) where TData : class
        {
            return ResolveKind(context.LocalIsEmpty, context.CloudIsEmpty) == ConflictResolutionKind.KeepLocal
                ? ConflictResolution<TData>.KeepLocal
                : ConflictResolution<TData>.TakeCloud;
        }

        internal static ConflictResolutionKind ResolveKind(bool localIsEmpty, bool cloudIsEmpty)
        {
            // Empty cloud never replaces local content
            if (cloudIsEmpty && !localIsEmpty)
            {
                return ConflictResolutionKind.KeepLocal;
            }

            return ConflictResolutionKind.TakeCloud;
        }
    }
}
