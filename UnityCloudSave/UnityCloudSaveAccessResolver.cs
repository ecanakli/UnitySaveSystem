using Unity.Services.CloudSave.Models;

namespace Ecanakli.SaveSystem.UnityCloudSave
{
    /// <summary>Pure mapping from CloudAccess plus a per-key Public override to the UGS AccessClass.</summary>
    internal static class UnityCloudSaveAccessResolver
    {
        /// <summary>Reading Protected data is allowed for the signed-in owner; the override always wins.</summary>
        public static AccessClass ResolveRead(CloudAccess access, bool isPublicOverride)
        {
            if (isPublicOverride)
            {
                return AccessClass.Public;
            }

            return access == CloudAccess.ServerOwned ? AccessClass.Protected : AccessClass.Default;
        }

        /// <summary>The client SDK can never write Protected; callers must reject ServerOwned writes before this.</summary>
        public static AccessClass ResolveWrite(bool isPublicOverride)
        {
            return isPublicOverride ? AccessClass.Public : AccessClass.Default;
        }
    }
}
