using Ecanakli.SaveSystem;
using NUnit.Framework;
using Unity.Services.CloudSave.Models;

namespace Ecanakli.SaveSystem.UnityCloudSave.Tests
{
    /// <summary>Pure CloudAccess + public-override -> AccessClass mapping. No SDK or network calls.</summary>
    [TestFixture]
    public sealed class UnityCloudSaveAccessResolverTests
    {
        [Test]
        public void ResolveRead_ClientOwned_NoOverride_ReturnsDefault()
        {
            Assert.That(UnityCloudSaveAccessResolver.ResolveRead(CloudAccess.ClientOwned, false), Is.EqualTo(AccessClass.Default));
        }

        [Test]
        public void ResolveRead_ServerOwned_NoOverride_ReturnsProtected()
        {
            Assert.That(UnityCloudSaveAccessResolver.ResolveRead(CloudAccess.ServerOwned, false), Is.EqualTo(AccessClass.Protected));
        }

        [Test]
        public void ResolveRead_ClientOwned_WithOverride_ReturnsPublic()
        {
            Assert.That(UnityCloudSaveAccessResolver.ResolveRead(CloudAccess.ClientOwned, true), Is.EqualTo(AccessClass.Public));
        }

        [Test]
        public void ResolveRead_ServerOwned_WithOverride_ReturnsPublic()
        {
            // The per-key override always wins, even for a ServerOwned request.
            Assert.That(UnityCloudSaveAccessResolver.ResolveRead(CloudAccess.ServerOwned, true), Is.EqualTo(AccessClass.Public));
        }

        [Test]
        public void ResolveWrite_NoOverride_ReturnsDefault()
        {
            Assert.That(UnityCloudSaveAccessResolver.ResolveWrite(false), Is.EqualTo(AccessClass.Default));
        }

        [Test]
        public void ResolveWrite_WithOverride_ReturnsPublic()
        {
            Assert.That(UnityCloudSaveAccessResolver.ResolveWrite(true), Is.EqualTo(AccessClass.Public));
        }
    }
}
