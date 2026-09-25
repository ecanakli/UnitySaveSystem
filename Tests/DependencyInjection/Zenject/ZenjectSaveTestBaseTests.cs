using System;
using NUnit.Framework;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    [TestFixture]
    internal sealed class ZenjectSaveTestBaseTests : ZenjectSaveTestBase
    {
        // Both cases run on one fixture instance; without the per-test reset the second one disposes nothing
        [TestCase(1)]
        [TestCase(2)]
        public void DisposeContainerDisposables_DisposesInEveryTestOfTheFixture(int run)
        {
            var disposable = new RecordingDisposable();
            Container.Bind<IDisposable>().FromInstance(disposable).AsCached();

            DisposeContainerDisposables();

            Assert.That(disposable.Disposed, Is.True, "Run " + run + " must dispose its own container.");
        }

        private sealed class RecordingDisposable : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose()
            {
                Disposed = true;
            }
        }
    }
}
