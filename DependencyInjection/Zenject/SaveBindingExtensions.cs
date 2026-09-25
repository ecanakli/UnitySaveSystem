using System;
using System.Collections.Generic;
using System.Linq;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Declares a game's SaveSlot with Zenject.</summary>
    public static class SaveBindingExtensions
    {
        /// <summary>
        /// Binds one TSlot instance to SaveSlot, TSlot and any additionalContracts, so SaveServiceInstaller's
        /// ResolveAll&lt;SaveSlot&gt;() finds it. Uses AsCached, not AsSingle: Zenject 6+ rejects two AsSingle
        /// creation bindings for the same concrete type, and a slot that also implements a listener interface
        /// (e.g. IRestoreListener) is exactly that case. Pass such interfaces as additionalContracts so they
        /// resolve to this same instance instead of binding them separately with BindInterfacesTo, which would
        /// create a second instance.
        /// </summary>
        public static ConcreteIdArgConditionCopyNonLazyBinder BindSaveSlot<TSlot>(this DiContainer container, params Type[] additionalContracts)
            where TSlot : SaveSlot
        {
            var contractTypes = new List<Type> { typeof(SaveSlot), typeof(TSlot) };
            if (additionalContracts != null)
            {
                contractTypes.AddRange(additionalContracts.Where(t => t != null));
            }

            // F9: de-duplicate. Zenject registers the provider once per entry in ContractTypes without
            // de-duplicating, so a repeated contract (SaveSlot, TSlot or a repeated additionalContracts entry)
            // would otherwise register the same instance twice under that contract.
            return container.Bind(contractTypes.Distinct().ToList()).To<TSlot>().AsCached();
        }
    }
}
