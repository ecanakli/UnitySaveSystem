using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    /// <summary>
    /// Shared setup for the Zenject integration tests. SaveServiceInstaller builds its AtomicFileStorage over
    /// SaveServiceOptions.RootDirectory, so every test points that at a throwaway temp directory instead of
    /// the real persistentDataPath. A bare test container has no manager bindings, so they are installed here.
    /// </summary>
    internal abstract class ZenjectSaveTestBase : ZenjectUnitTestFixture
    {
        private readonly List<string> _tempRoots = new List<string>();
        private bool _disposablesDisposed;

        /// <summary>Creates a unique temp directory and returns its path; deleted in TearDown.</summary>
        protected string CreateTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "EcanakliSaveSystemZenjectTests", Guid.NewGuid().ToString("N"));
            _tempRoots.Add(root);
            return root;
        }

        /// <summary>A bare test container has no manager bindings; a real context installs them for every scene.</summary>
        public override void Setup()
        {
            base.Setup();

            // NUnit runs every test of a fixture on one instance, so per-test state resets here
            _disposablesDisposed = false;
            _tempRoots.Clear();
            ZenjectManagersInstaller.Install(Container);
        }

        /// <summary>
        /// Disposes every IDisposable bound in this container, once. Zenject asserts when DisposableManager
        /// itself is disposed twice, so a test that needs the order must come through here, not resolve it.
        /// </summary>
        protected void DisposeContainerDisposables()
        {
            if (_disposablesDisposed)
            {
                return;
            }

            _disposablesDisposed = true;
            Container.TryResolve<DisposableManager>()?.Dispose();
        }

        /// <summary>Options rooted in a throwaway temp directory so real save data never lands under persistentDataPath.</summary>
        protected SaveServiceOptions CreateIsolatedOptions()
        {
            return new SaveServiceOptions
            {
                OffloadIo = false,
                Logger = new UnityDebugSaveLogger(),
                RootDirectory = CreateTempRoot(),
            };
        }

        public override void Teardown()
        {
            Exception disposeError = null;
            try
            {
                DisposeContainerDisposables();
            }
            catch (Exception exception)
            {
                // Reported below, never swallowed: a hidden disposal error once masked a duplicate IDisposable binding
                disposeError = exception;
            }
            finally
            {
                foreach (string root in _tempRoots)
                {
                    TryDeleteDirectory(root);
                }

                base.Teardown();
            }

            if (disposeError != null)
            {
                throw new InvalidOperationException("Disposing the test container failed: " + disposeError.Message, disposeError);
            }
        }

        /// <summary>
        /// Returns the result of a UniTask that must already be complete (OffloadIo = false, no gate contention).
        /// Throws a descriptive error otherwise, since EditMode tests cannot pump the player loop.
        /// </summary>
        protected static T RunSync<T>(UniTask<T> task)
        {
            if (!task.Status.IsCompleted())
            {
                throw new InvalidOperationException("The task did not complete synchronously; it needs player-loop ticks.");
            }

            return task.GetAwaiter().GetResult();
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Best effort cleanup only
            }
        }
    }
}
