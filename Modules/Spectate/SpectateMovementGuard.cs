#nullable enable

using System.Reflection;
using BepInEx.Logging;
using DeathHeadHopperVRBridge.Modules.Config;
using DeathHeadHopperVRBridge.Modules.Logging;
using HarmonyLib;
using RepoXR.Patches;
using Logger = BepInEx.Logging.Logger;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    [HarmonyPatch]
    internal static class SpectateMovementGuard
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("DeathHeadHopperFix-VR.HeadBridge");

        private static MethodBase? TargetMethod()
        {
            var patchesType = AccessTools.TypeByName("RepoXR.Patches.SpectatePatches");
            return patchesType == null ? null : AccessTools.Method(patchesType, "<InputPatch>g__GetRotationX|5_0");
        }

        private static bool Prefix(ref float __result)
        {
            return TryGuard(ref __result);
        }

        [HarmonyPatch]
        internal static class SpectateMovementGuardY
        {
            private static MethodBase? TargetMethod()
            {
                var patchesType = AccessTools.TypeByName("RepoXR.Patches.SpectatePatches");
                return patchesType == null ? null : AccessTools.Method(patchesType, "<InputPatch>g__GetRotationY|5_1");
            }

            private static bool Prefix(ref float __result)
            {
                return SpectateMovementGuard.TryGuard(ref __result);
            }
        }

        private static bool TryGuard(ref float result)
        {
            if (SpectateHeadBridge.IsGripPressedForCamera())
            {
                return true;
            }

            if (FeatureFlags.DebugSpectateGuard && LogLimiter.Allow("MovementGuard.Override", 0.25f))
            {
                Log.LogDebug($"{SpectateHeadBridge.ModuleTag} Movement guard zeroed rotation because grip is not pressed");
            }

            result = 0f;
            return false;
        }
    }
}
