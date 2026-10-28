using System;

namespace PerfeBT
{
    // Whether a mod assembly is loaded, without touching any of its types. Fixes that only apply when an optional
    // mod is present check this before calling (and precompiling) the holder that uses the mod's members directly.
    internal static class ModAssemblies
    {
        internal static bool Loaded(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name)
                    return true;
            return false;
        }
    }
}
