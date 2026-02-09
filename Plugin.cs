#nullable enable

using BepInEx;
using HarmonyLib;
using DeathHeadHopperVRBridge.Modules.Config;
using DeathHeadHopperVRBridge.Modules.Spectate;
using RepoXR;

namespace DeathHeadHopperVRBridge
{
    /// <summary>Bootstraps the bridge by initializing configuration and Harmony patches.</summary>
    [BepInDependency("Cronchy.DeathHeadHopper")]
    [BepInDependency("AdrenSnyder.DeathHeadHopperFix")]
    [BepInDependency("io.daxcess.repoxr")]
    [BepInPlugin("AdrenSnyder.DeathHeadHopperVRBridge", "Death Head Hopper - VRBridge", "0.1.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private Harmony? _harmony;

        /// <summary>Initializes the configuration and applies Harmony patches.</summary>
        private void Awake()
        {
            ConfigManager.Initialize(Config);

            if (RepoXR.Plugin.Flags.HasFlag(Flags.VR))
            {
                _harmony = new Harmony("DeathHeadHopperFix-VR.Spectate");
                _harmony.PatchAll();
                VrAbilityBarBridge.EnsureAttached(gameObject);
                VanillaAbilityInputBridge.EnsureAttached(gameObject);
                Logger.LogInfo("DeathHeadHopperFix-VR bridge ready (spectate head bridge enabled).");
            }
            else
            {
                Logger.LogWarning("RepoXR did not initialize VR mode, keeping Bridge disabled.");
            }
            
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
