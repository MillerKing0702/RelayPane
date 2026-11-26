using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CollaborationChatPreview
{
    internal static class EmbeddedLibraries
    {
        private static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private static bool initialized;

        public static void Initialize()
        {
            lock (loaded)
            {
                if (initialized) return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                initialized = true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            switch (name)
            {
                case "Markdig":
                case "System.Memory":
                case "System.Buffers":
                case "System.Numerics.Vectors":
                case "System.Runtime.CompilerServices.Unsafe":
                    break;
                default:
                    return null;
            }

            lock (loaded)
            {
                Assembly assembly;
                if (loaded.TryGetValue(name, out assembly)) return assembly;
                using (Stream resource = typeof(EmbeddedLibraries).Assembly.GetManifestResourceStream("Markdown." + name + ".dll"))
                using (var bytes = new MemoryStream())
                {
                    if (resource == null) return null;
                    resource.CopyTo(bytes);
                    assembly = Assembly.Load(bytes.ToArray());
                    loaded.Add(name, assembly);
                    return assembly;
                }
            }
        }
    }
}
