using System;
using System.Collections.Generic;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>
    /// Adds every resolved IRestoreListener and IProfileDeactivatingListener to the save service on Initialize,
    /// removes them on Dispose. Bound with FromMethod and explicit new: no Zenject reflection, no link.xml entry needed.
    /// </summary>
    internal sealed class SaveListenerRegistrar : IInitializable, IDisposable
    {
        private readonly ISaveService _saveService;
        private readonly List<IRestoreListener> _restoreListeners;
        private readonly List<IProfileDeactivatingListener> _deactivationListeners;
        private bool _disposed;

        internal SaveListenerRegistrar(
            ISaveService saveService,
            List<IRestoreListener> restoreListeners,
            List<IProfileDeactivatingListener> deactivationListeners)
        {
            _saveService = saveService ?? throw new ArgumentNullException(nameof(saveService));
            _restoreListeners = restoreListeners ?? new List<IRestoreListener>();
            _deactivationListeners = deactivationListeners ?? new List<IProfileDeactivatingListener>();
        }

        public void Initialize()
        {
            for (int i = 0; i < _restoreListeners.Count; i++)
            {
                _saveService.AddRestoreListener(_restoreListeners[i]);
            }

            for (int i = 0; i < _deactivationListeners.Count; i++)
            {
                _saveService.AddDeactivationListener(_deactivationListeners[i]);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // RemoveRestoreListener/RemoveDeactivationListener are no-ops after the service itself is disposed
            for (int i = 0; i < _restoreListeners.Count; i++)
            {
                _saveService.RemoveRestoreListener(_restoreListeners[i]);
            }

            for (int i = 0; i < _deactivationListeners.Count; i++)
            {
                _saveService.RemoveDeactivationListener(_deactivationListeners[i]);
            }
        }
    }
}
