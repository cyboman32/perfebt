using BattleTech;

namespace PerfeBT
{
    // UnityGameInstance.CheckInternetConnectivity does a synchronous HttpWebRequest with a 5 s timeout on the
    // main thread during startup (measured 1.5-5.0 s). The HBS online services it gates are gone; report
    // offline immediately, which is what the game does whenever the request times out.
    internal static class InternetCheck
    {
        internal static bool Prefix(UnityGameInstance __instance)
        {
            __instance.OnInternetConnectivityResult(false);
            Log.Write("[fix] internet connectivity check skipped (reported offline)");
            return false;
        }
    }
}
