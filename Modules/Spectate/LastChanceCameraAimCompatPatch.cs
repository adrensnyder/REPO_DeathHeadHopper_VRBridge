#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Logging;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal static class LastChanceCameraAimCompatPatch
    {
        private const string LogKey = "LastChance.CameraForce.Noop.VR";
        private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] CameraForceModuleTypeNames =
        {
            "DHHFLastChanceMode.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule",
            "DeathHeadHopperFix.Modules.Gameplay.LastChance.Monsters.Pipeline.LastChanceMonstersCameraForceLockModule"
        };
        private static readonly string[] RuntimeOrchestratorTypeNames =
        {
            "DHHFLastChanceMode.Modules.Gameplay.LastChance.Runtime.LastChanceRuntimeOrchestrator",
            "DeathHeadHopperFix.Modules.Gameplay.LastChance.Runtime.LastChanceRuntimeOrchestrator"
        };

        private static PropertyInfo? s_runtimeActiveProperty;
        private static bool s_applied;

        internal static bool TryApply(Harmony harmony)
        {
            if (s_applied)
            {
                return true;
            }

            if (!TryResolveTarget(out var target))
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

        private static bool TryResolveTarget(out MethodInfo? target)
        {
            var type = TryResolveType(CameraForceModuleTypeNames);

            target = type == null
                ? null
                : AccessTools.Method(type, "TryForceSpectateAimTo", new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.GameObject) });
            return target != null;
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
                var type = TryResolveType(RuntimeOrchestratorTypeNames);
                s_runtimeActiveProperty = type?.GetProperty("IsRuntimeActive", StaticAny);
            }

            return s_runtimeActiveProperty?.GetValue(null) as bool? ?? false;
        }

        private static Type? TryResolveType(IReadOnlyList<string> fullNames)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < fullNames.Count; i++)
            {
                var fullName = fullNames[i];
                for (var a = 0; a < assemblies.Length; a++)
                {
                    var resolved = assemblies[a].GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            return null;
        }
    }
}
