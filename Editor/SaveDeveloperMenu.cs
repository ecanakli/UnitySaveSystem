using System.IO;
using UnityEditor;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>Developer utilities for the local save folder: reveal it, or wipe it.</summary>
    internal static class SaveDeveloperMenu
    {
        private const string OpenFolderMenuPath = "Tools/Save System/Open Save Folder";
        private const string DeleteAllMenuPath = "Tools/Save System/Delete All Saves...";

        [MenuItem(OpenFolderMenuPath)]
        private static void OpenSaveFolder()
        {
            string root = SaveFolders.DefaultRootDirectory;
            if (!Directory.Exists(root))
            {
                bool create = EditorUtility.DisplayDialog(
                    "Save System",
                    $"The save folder does not exist yet:\n{root}",
                    "Create It",
                    "Cancel");
                if (!create)
                {
                    return;
                }

                Directory.CreateDirectory(root);
            }

            EditorUtility.RevealInFinder(root);
        }

        [MenuItem(OpenFolderMenuPath, true)]
        private static bool OpenSaveFolderValidate()
        {
            return !EditorApplication.isPlaying;
        }

        [MenuItem(DeleteAllMenuPath)]
        private static void DeleteAllSaves()
        {
            string root = SaveFolders.DefaultRootDirectory;
            bool confirmed = EditorUtility.DisplayDialog(
                "Delete All Saves",
                $"This deletes every local save under:\n{root}\n\nThis cannot be undone. Continue?",
                "Delete",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            LocalWipeStatus status = SaveFolders.DeleteAllLocalData(root);
            EditorUtility.DisplayDialog("Delete All Saves", DescribeStatus(status), "OK");
        }

        [MenuItem(DeleteAllMenuPath, true)]
        private static bool DeleteAllSavesValidate()
        {
            return !EditorApplication.isPlaying;
        }

        private static string DescribeStatus(LocalWipeStatus status)
        {
            switch (status)
            {
                case LocalWipeStatus.Deleted:
                    return "Deleted.";
                case LocalWipeStatus.NothingToDelete:
                    return "Nothing to delete.";
                case LocalWipeStatus.InUse:
                    return "A running SaveService owns this folder; nothing was deleted.";
                case LocalWipeStatus.Failed:
                    return "Delete failed; some data may remain. See the Console for details.";
                default:
                    return status.ToString();
            }
        }
    }
}
