using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>SaveFolders: scoped local wipe, live-root protection and the default root.</summary>
    [TestFixture]
    public sealed class SaveFoldersTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "EcanakliSaveSystemTests", "folders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void DeleteAllLocalData_DeletesOnlyDeviceAndProfiles()
        {
            WriteFile("device/device.json");
            WriteFile("profiles/guest/player.json");
            WriteFile("profiles/acc-a/profile.json");
            WriteFile("tools/keep.txt");
            WriteFile("profiles_backup/keep.json");
            WriteFile("keep.json");

            LocalWipeStatus status = SaveFolders.DeleteAllLocalData(_root);

            Assert.That(status, Is.EqualTo(LocalWipeStatus.Deleted));
            Assert.That(Directory.Exists(Full("device")), Is.False);
            Assert.That(Directory.Exists(Full("profiles")), Is.False);
            Assert.That(File.Exists(Full("tools/keep.txt")), Is.True);
            Assert.That(File.Exists(Full("profiles_backup/keep.json")), Is.True);
            Assert.That(File.Exists(Full("keep.json")), Is.True);
            Assert.That(Directory.Exists(_root), Is.True);
        }

        [Test]
        public void DeleteAllLocalData_OnlyProfilesPresent_Deleted()
        {
            WriteFile("profiles/local-alice/player.json");

            Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.Deleted));
            Assert.That(Directory.Exists(Full("profiles")), Is.False);
        }

        [Test]
        public void DeleteAllLocalData_NothingPresent_NothingToDelete()
        {
            WriteFile("other/file.json");
            string missingRoot = Path.Combine(_root, "missing-root");

            Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.NothingToDelete));
            Assert.That(SaveFolders.DeleteAllLocalData(missingRoot), Is.EqualTo(LocalWipeStatus.NothingToDelete));
            Assert.That(File.Exists(Full("other/file.json")), Is.True);
        }

        [Test]
        public void DeleteAllLocalData_LiveRoot_ReturnsInUse_AndDeletesNothing()
        {
            WriteFile("device/device.json");
            WriteFile("profiles/guest/player.json");

            SaveFolders.RegisterLiveRoot(_root + Path.DirectorySeparatorChar);
            try
            {
                Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.InUse));
                Assert.That(File.Exists(Full("device/device.json")), Is.True);
                Assert.That(File.Exists(Full("profiles/guest/player.json")), Is.True);
            }
            finally
            {
                SaveFolders.UnregisterLiveRoot(_root);
            }

            Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.Deleted));
        }

        [Test]
        public void DeleteAllLocalData_LiveServiceRoot_InUseUntilDisposed()
        {
            WriteFile("profiles/guest/player.json");
            var setup = new TestServiceSetup { Slots = new SaveSlot[] { new ProfileSlot() }, RootDirectory = _root };

            using (TestServiceContext context = TestServiceFactory.Create(setup))
            {
                Assert.That(SaveFolders.IsLiveRoot(_root), Is.True);
                Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.InUse));
                Assert.That(File.Exists(Full("profiles/guest/player.json")), Is.True);
            }

            Assert.That(SaveFolders.IsLiveRoot(_root), Is.False);
            Assert.That(SaveFolders.DeleteAllLocalData(_root), Is.EqualTo(LocalWipeStatus.Deleted));
        }

        [Test]
        public void LiveRoot_IsReferenceCounted()
        {
            try
            {
                Assert.That(SaveFolders.RegisterLiveRoot(_root), Is.True);
                Assert.That(SaveFolders.RegisterLiveRoot(_root), Is.False, "A second registration reports the root was already live.");

                SaveFolders.UnregisterLiveRoot(_root);
                Assert.That(SaveFolders.IsLiveRoot(_root), Is.True);

                SaveFolders.UnregisterLiveRoot(_root);
                Assert.That(SaveFolders.IsLiveRoot(_root), Is.False);

                Assert.DoesNotThrow(() => SaveFolders.UnregisterLiveRoot(_root));
            }
            finally
            {
                while (SaveFolders.IsLiveRoot(_root))
                {
                    SaveFolders.UnregisterLiveRoot(_root);
                }
            }
        }

        [Test]
        public void DeleteAllLocalData_NullOrEmptyRoot_Throws()
        {
            Assert.Throws<ArgumentException>(() => SaveFolders.DeleteAllLocalData(null));
            Assert.Throws<ArgumentException>(() => SaveFolders.DeleteAllLocalData(string.Empty));
        }

        [Test]
        public void DefaultRootDirectory_IsSavesUnderPersistentDataPath()
        {
            string expected = Path.Combine(Application.persistentDataPath, SaveFolders.SavesDirectoryName);

            Assert.That(SaveFolders.DefaultRootDirectory, Is.EqualTo(expected));
            Assert.That(SaveFolders.DefaultRootDirectory, Does.StartWith(Application.persistentDataPath));
            Assert.That(new AtomicFileStorage().RootDirectory, Is.EqualTo(Path.GetFullPath(expected)));
        }

        private void WriteFile(string relativePath)
        {
            string path = Full(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{}");
        }

        private string Full(string relativePath)
        {
            return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
