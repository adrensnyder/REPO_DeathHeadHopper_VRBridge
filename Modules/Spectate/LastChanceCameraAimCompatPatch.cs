#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Logging;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    [HarmonyPatch]
    internal static class LastChanceCameraAimCompatPatch
    {
        private const string LogKey = "LastChance.CameraForce.Noop.VR";
        private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static PropertyInfo? s_runtimeActiveProperty;

        private static MethodBase? TargetMethod()
        {
            var type =
                AccessTools.TypeByName("DHHFLastChanceMode.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule") ??
                AccessTools.TypeByName("DeathHeadHopperFix.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule");

            return type == null
                ? null
                : AccessTools.Method(type, "TryForceSpectateAimTo", new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.GameObject) });
        }

        private static bool Prefix()
        {
            if (!SpectateHeadBridge.VrModeActive || !IsLastChanceRuntimeActive())
            {
                return true;
            }

            if (LogLimiter.Allow(LogKey, 5f))
            {
                SpectateHeadBridge.Log.LogInfo($"{SpectateHeadBridge.ModuleTag} LastChance camera-force disabled in VR to avoid HMD/spectate drift.");
            }

            return false;
        }

        private static bool IsLastChanceRuntimeActive()
        {
            if (s_runtimeActiveProperty == null)
            {
                var type =
                    AccessTools.TypeByName("DHHFLastChanceMode.Modules.Gameplay.LastChance.Runtime.LastChanceRuntimeOrchestrator") ??
                    AccessTools.TypeByName("DeathHeadHopperFix.Modules.Gameplay.LastChance.Runtime.LastChanceRuntimeOrchestrator");
                s_runtimeActiveProperty = type?.GetProperty("IsRuntimeActive", StaticAny);
            }

            return s_runtimeActiveProperty?.GetValue(null) as bool? ?? false;
        }
    }
}
