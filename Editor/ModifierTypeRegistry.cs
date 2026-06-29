using System;
using System.Collections.Generic;
using System.Linq;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Discovers the concrete <see cref="ModifierBehaviour"/> types available to add to a streamer's stack
    /// (the "Add Modifier" menu). Mirrors UE's modifier-type discovery (which builds its add menu from the
    /// registered <c>UModifierComponent</c> subclasses).
    /// </summary>
    public static class ModifierTypeRegistry
    {
        public readonly struct Entry
        {
            public readonly Type Type;
            public readonly string DisplayName;
            public Entry(Type type, string displayName) { Type = type; DisplayName = displayName; }
        }

        static List<Entry> _entries;

        /// <summary>All concrete modifier-wrapper types, sorted with the base modifier(s) first then by name.</summary>
        public static IReadOnlyList<Entry> Entries
        {
            get
            {
                if (_entries != null) return _entries;

                var baseType = typeof(ModifierBehaviour);
                _entries = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .Where(t => t != null && !t.IsAbstract && baseType.IsAssignableFrom(t))
                    .Select(t => new Entry(t, Prettify(t.Name)))
                    .OrderBy(e => IsBase(e.Type) ? 0 : 1)
                    .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                    .ToList();
                return _entries;
            }
        }

        static bool IsBase(Type t)
        {
            // A base wrapper reports IsBaseModifier == true; probe a throwaway instance is unsafe (MonoBehaviour),
            // so fall back to the name convention used by the concrete wrappers.
            return t.Name.IndexOf("Base", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly a)
        {
            try { return a.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        // "NoiseModifierBehaviour" -> "Noise Modifier".
        static string Prettify(string typeName)
        {
            string n = typeName.EndsWith("Behaviour") ? typeName[..^"Behaviour".Length] : typeName;
            var sb = new System.Text.StringBuilder(n.Length + 4);
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
                sb.Append(n[i]);
            }
            return sb.ToString();
        }
    }
}
