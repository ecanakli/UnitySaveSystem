using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    // Immutable list helpers for result types
    internal static class ResultLists
    {
        public static IReadOnlyList<T> Copy<T>(IEnumerable<T> source)
        {
            if (source == null)
            {
                return Array.Empty<T>();
            }

            var list = new List<T>(source);
            return list.Count == 0 ? (IReadOnlyList<T>)Array.Empty<T>() : list.AsReadOnly();
        }
    }
}
