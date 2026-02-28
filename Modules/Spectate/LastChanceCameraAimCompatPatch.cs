#nullable enable

using DHHFLastChanceMode.Modules.Gameplay.LastChance.Monsters.Pipeline;
using DHHFLastChanceMode.Modules.Gameplay.LastChance.Runtime;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Logging;
using System.Linq;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal static class LastChanceCameraAimCompatPatch
    {
        private const string LogKey = "LastChance.CameraForce.Noop.VR";
        private static bool s_applied;

        internal static bool TryApply(Harmony harmony)
        {
            if (s_applied)
            {
                return true;
            }

            var target = AccessTools.Method(
                typeof(LastChanceMonstersCameraForceLockModule),
                nameof(LastChanceMonstersCameraForceLockModule.TryForceSpectateAimTo),
                new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.GameObject) });
            if (target == null)
            {
                return false;
            }

            var prefix = AccessTools.Method(typeof(LastChanceCameraAimCompatPatch), nameof(Prefix));
            if (prefix == null)
            {
                return false;
            }

            var patchInfo = Harmony.GetPatchInfo(target);
            if (patchInfo?.Prefixes.Any(p => p.owner == harmony.Id) == true)
            {
                s_applied = true;
                return true;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            s_applied = true;
            return true;
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

        private static bool IsLastChanceRuntimeActive() => LastChanceRuntimeOrchestrator.IsRuntimeActive;
    }
}
