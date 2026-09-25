using System;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>
    /// Attaches a SaveLifecycleDriver on Initialize and disposes the handle on Dispose.
    /// Bound with FromMethod and explicit new: no Zenject reflection, no link.xml entry needed.
    /// </summary>
    internal sealed class SaveLifecycleHost : IInitializable, IDisposable
    {
        private readonly ISaveService _saveService;
        private IDisposable _handle;

        internal SaveLifecycleHost(ISaveService saveService)
        {
            _saveService = saveService ?? throw new ArgumentNullException(nameof(saveService));
        }

        public void Initialize()
        {
            _handle = SaveLifecycleDriver.Attach(_saveService);
        }

        public void Dispose()
        {
            IDisposable handle = _handle;
            _handle = null;
            handle?.Dispose();
        }
    }
}
