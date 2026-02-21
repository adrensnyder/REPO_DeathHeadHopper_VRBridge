#nullable enable

using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Logging;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    [HarmonyPatch]
    internal static class LastChanceCameraAimCompatPatch
    {
        private const string LogKey = "LastChance.CameraForce.Noop.VR";
        private const string LastChancePluginGuid = "AdrenSnyder.DHHFLastChanceMode";

        private static MethodBase? TargetMethod()
        {
            // Apply this compatibility patch only when LastChance is actually loaded.
            if (!Chainloader.PluginInfos.ContainsKey(LastChancePluginGuid))
            {
                return null;
            }

            var type =
                AccessTools.TypeByName("DHHFLastChanceMode.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule") ??
                AccessTools.TypeByName("DeathHeadHopperFix.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule");

            return type == null
                ? null
                : AccessTools.Method(type, "TryForceSpectateAimTo", new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.GameObject) });
        }

        private static bool Prefix()
        {
            if (!SpectateHeadBridge.VrModeActive)
            {
                return true;
            }

            if (LogLimiter.Allow(LogKey, 5f))
            {
                SpectateHeadBridge.Log.LogInfo($"{SpectateHeadBridge.ModuleTag} LastChance camera-force disabled in VR to avoid HMD/spectate drift.");
            }

            return false;
        }
    }
}
