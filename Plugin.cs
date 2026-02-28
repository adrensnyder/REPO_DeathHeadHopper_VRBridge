#nullable enable

using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Config;
using DeathHeadHopperVRBridge.Modules.Spectate;
using RepoXR;
using UnityEngine;

namespace DeathHeadHopperVRBridge
{
    /// <summary>Bootstraps the bridge by initializing configuration and Harmony patches.</summary>
    [BepInDependency("Cronchy.DeathHeadHopper")]
    [BepInDependency("AdrenSnyder.DeathHeadHopperFix")]
    [BepInDependency("AdrenSnyder.DHHFLastChanceMode", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("io.daxcess.repoxr")]
    [BepInPlugin("AdrenSnyder.DeathHeadHopperVRBridge", "Death Head Hopper - VRBridge", "0.1.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string BuildStamp = "C13-ability-pipeline-probe";
        private const string HarmonyId = "DeathHeadHopperFix-VR.Spectate";
        private const string LastChancePluginGuid = "AdrenSnyder.DHHFLastChanceMode";

        private Harmony? _harmony;
        private Coroutine? _lastChanceCompatRoutine;

        /// <summary>Initializes the configuration and applies Harmony patches.</summary>
        private void Awake()
        {
            ConfigManager.Initialize(Config);

            if (RepoXR.Plugin.Flags.HasFlag(Flags.VR))
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll();
                _lastChanceCompatRoutine = StartCoroutine(ApplyOptionalLastChanceCompat());

                // Legacy VR cursor-based slot selector (Grab/Interact/Push confirm path).
                // Kept in codebase for fallback/reference, but intentionally disabled because
                // current direction uses vanilla-equivalent input pipeline (grip + configured
                // binding -> DHH HandleInputDown/Hold/Up), matching flat Inventory behavior.
                // VrAbilityBarBridge.EnsureAttached(gameObject);
                VanillaAbilityInputBridge.EnsureAttached(gameObject);
                Logger.LogInfo($"DeathHeadHopperFix-VR bridge ready (spectate head bridge enabled). build={BuildStamp}");
            }
            else
            {
                Logger.LogWarning("RepoXR did not initialize VR mode, keeping Bridge disabled.");
            }
        }

        private IEnumerator ApplyOptionalLastChanceCompat()
        {
            const int attempts = 4;
            const float retryDelaySeconds = 1f;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var harmony = _harmony;
                if (harmony == null)
                {
                    yield break;
                }

                if (!Chainloader.PluginInfos.ContainsKey(LastChancePluginGuid))
                {
                    Logger.LogInfo("[LastChanceCompat] optional plugin not found. LastChance compat patches remain dormant.");
                    _lastChanceCompatRoutine = null;
                    yield break;
                }

                if (LastChanceCameraAimCompatPatch.TryApply(harmony))
                {
                    Logger.LogInfo("[LastChanceCompat] camera-force patch applied.");
                    _lastChanceCompatRoutine = null;
                    yield break;
                }

                if (attempt < attempts)
                {
                    yield return new WaitForSeconds(retryDelaySeconds);
                    continue;
                }

                Logger.LogWarning("[LastChanceCompat] plugin detected but patch target was not found. Keeping compat dormant.");
                _lastChanceCompatRoutine = null;
            }
        }

        private void OnDestroy()
        {
            if (_lastChanceCompatRoutine != null)
            {
                StopCoroutine(_lastChanceCompatRoutine);
                _lastChanceCompatRoutine = null;
            }

            _harmony?.UnpatchSelf();
        }
    }
}
