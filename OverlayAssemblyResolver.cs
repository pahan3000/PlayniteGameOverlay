using System;
using System.IO;
using System.Reflection;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    // The Playnite host shares one AppDomain with all its extensions.
    // This resolver is intentionally limited to this extension's bundled dependencies.
    internal static class OverlayAssemblyResolver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly object Gate = new object();
        private static bool installed;
        private static readonly string[] Names = {
            "System.Memory", "System.Buffers", "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe", "System.Threading.Tasks.Extensions",
            "System.Text.Json", "System.Text.Encodings.Web", "Microsoft.Bcl.AsyncInterfaces"
        };

        internal static void Install()
        {
            lock (Gate)
            {
                if (installed) return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                installed = true;
                Logger.Info("Overlay dependency resolver installed at " + Path.GetDirectoryName(typeof(OverlayAssemblyResolver).Assembly.Location));
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                bool allowed = false;
                foreach (var name in Names)
                    if (string.Equals(requested.Name, name, StringComparison.OrdinalIgnoreCase))
                    { allowed = true; break; }
                if (!allowed) return null;
                string dir = Path.GetDirectoryName(typeof(OverlayAssemblyResolver).Assembly.Location);
                string path = Path.Combine(dir, requested.Name + ".dll");
                if (!File.Exists(path))
                {
                    Logger.Error("Overlay dependency unavailable: " + args.Name + "; searched " + path);
                    return null;
                }
                var identity = AssemblyName.GetAssemblyName(path);
                // Do not substitute a different public key or major/minor assembly version.
                if (!string.Equals(identity.Name, requested.Name, StringComparison.OrdinalIgnoreCase)
                    || (requested.Version != null && identity.Version != requested.Version))
                {
                    Logger.Error("Overlay dependency identity mismatch: requested " + args.Name + "; file " + identity.FullName);
                    return null;
                }
                var loaded = Assembly.LoadFrom(path);
                Logger.Info("Overlay dependency resolved " + loaded.FullName + " from " + path);
                return loaded;
            }
            catch (Exception ex)
            {
                Logger.Error("Overlay dependency resolution failed for " + args.Name + ": " + ex);
                return null;
            }
        }
    }
}
