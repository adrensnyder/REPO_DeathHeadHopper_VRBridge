#nullable enable

namespace DeathHeadHopperVRBridge.Modules.Config
{
    // Internal-only debug switches.
    // Not exposed as FeatureFlags options and intended only for focused diagnostics.
    internal static class InternalDebugConfig
    {
        // Enables extra logs for spectate movement guard state transitions and gating.
        public static bool DebugSpectateGuard = false;

        // Enables verbose logs for movement direction source/resolution decisions.
        public static bool DebugMovementDirection = false;

        // Logs raw controller orientation samples used during movement/aim calculations.
        public static bool LogControllerOrientation = false;

        // Enables logs related to head-camera alignment and correction behavior.
        public static bool DebugHeadAlignment = false;

        // Logs ability input trigger resolution (InputKey/RepoXR) and jump suppression gates.
        public static bool DebugAbilityInputFlow = false;
    }
}
