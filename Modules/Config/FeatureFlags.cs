#nullable enable

namespace DeathHeadHopperVRBridge.Modules.Config
{
    internal static class FeatureFlags
    {
        [FeatureConfigEntry("Spectate Movement", "Defines the forward vector used to rotate jump direction. Recommended options are HeadRaycast/ControllerRaycast, which use first hit from POV then head->hit projected on the horizontal plane.", Options = new[] { "HeadRaycast", "ControllerRaycast" }, HostControlled = false)]
        public static string MovementDirectionSource = "ControllerRaycast";

        [FeatureConfigEntry("Spectate Movement", "Maximum distance (meters) for POV raycasts used by HeadRaycast/ControllerRaycast movement sources.", Min = 2f, Max = 200f, HostControlled = false)]
        public static float MovementRaycastDistance = 40f;

        [FeatureConfigEntry("Spectate Movement", "Render the RepoXR ray line for ControllerRaycast while spectating the head.", HostControlled = false)]
        public static bool ShowControllerRayLine = true;

        [FeatureConfigEntry("Spectate Movement", "Line length (meters) forced on the active RepoXR controller ray while DeathHead spectate raycast control is active. Vanilla RepoXR length management is restored outside this context.", Min = 1f, Max = 200f, HostControlled = false)]
        public static float ControllerRayLineLength = 20f;

        [FeatureConfigEntry("Spectate Movement", "Show a red marker at the current controller ray collision point used by spectate movement aiming.", HostControlled = false)]
        public static bool ShowControllerRayHitMarker = true;

        [FeatureConfigEntry("Spectate Movement", "Size of the red marker shown at the controller ray collision point.", Min = 0.005f, Max = 0.2f, HostControlled = false)]
        public static float ControllerRayHitMarkerSize = 0.03f;

        [FeatureConfigEntry("Spectate Movement", "When using ControllerRaycast, ignore small horizontal stick drift while pushing forward/backward.", Min = 0f, Max = 0.5f, HostControlled = false)]
        public static float ControllerRaycastXAxisDeadzone = 0.05f;

        [FeatureConfigEntry("Spectate Camera", "Distance that the spectator camera stays behind the death head.", Min = 0.05f, Max = 1.5f, HostControlled = false)]
        public static float HeadCameraDistance = 0.45f;

        [FeatureConfigEntry("Spectate Camera", "Vertical offset applied to the death head when aligning the camera.", Min = -0.2f, Max = 1.5f, HostControlled = false)]
        public static float HeadCameraHeightOffset = 0.5f;

        [FeatureConfigEntry("Spectate Camera", "How fast the spectator camera lerps back to the default position.", Min = 1f, Max = 12f, HostControlled = false)]
        public static float HeadCameraLerpSpeed = 8f;

        [FeatureConfigEntry("Spectate VR Ability", "Defines how DeathHeadHopper abilities are aimed. Recommended options are HeadRaycast/ControllerRaycast, which cast from POV to first hit and then aim from head to that point.", Options = new[] { "HeadRaycast", "ControllerRaycast" }, HostControlled = false)]
        public static string AbilityDirectionSource = "ControllerRaycast";

        [FeatureConfigEntry("Spectate VR Ability", "Maximum distance (meters) for the POV raycast used by HeadRaycast/ControllerRaycast.", Min = 2f, Max = 200f, HostControlled = false)]
        public static float AbilityRaycastDistance = 60f;

        [FeatureConfigEntry("Spectate VR Ability", "When true, POV raycast direction is flattened to the horizon plane before tracing.", HostControlled = false)]
        public static bool AbilityRaycastUseHorizon = false;

        [FeatureConfigEntry("Spectate VR Ability", "Hand used to hold the grip that activates the spectate ability cursor. Valid values: Auto (opposite the selected main hand in RepoXR), Left, Right.", Options = new[] { "Auto", "Left", "Right" }, HostControlled = false)]
        public static string AbilityGripPreference = "Auto";

        [FeatureConfigEntry("Spectate VR Ability", "Action name used to activate the selected slot. Runtime discovery from RepoXR actions is also used, then static fallbacks.",
            Options = new[]
            {
                "VR Actions/ResetHeight",
                "VR Actions/Interact",
                "VR Actions/Push",
            }, HostControlled = false)]
        public static string AbilityActivateAction = "VR Actions/ResetHeight";

        [FeatureConfigEntry("Spectate VR Direction", "Action name used to activate slot 2. Runtime discovery from RepoXR actions is also used, then static fallbacks.",
            Options = new[]
            {
                "VR Actions/ResetHeight",
                "VR Actions/Interact",
                "VR Actions/Push",
            }, HostControlled = false)]
        public static string AbilityDirectionAction = "VR Actions/Interact";

        [FeatureConfigEntry("Spectate VR Ability", "Distance in meters at which the VR ability cursor floats in front of the view.", Min = 0.2f, Max = 2f, HostControlled = false)]
        public static float AbilityCursorDistance = 0.6f;

        [FeatureConfigEntry("Spectate VR Ability", "Vertical offset applied to the ability cursor relative to the view origin.", Min = -1f, Max = 1f, HostControlled = false)]
        public static float AbilityCursorVerticalOffset = -0.25f;

        [FeatureConfigEntry("Spectate VR Ability", "Horizontal spacing between the ability markers.", Min = 0.1f, Max = 1f, HostControlled = false)]
        public static float AbilityCursorSpacing = 0.35f;

        [FeatureConfigEntry("Spectate VR Ability", "Base scale applied to the ability markers.", Min = 0.04f, Max = 0.5f, HostControlled = false)]
        public static float AbilityCursorScale = 0.15f;

        [FeatureConfigEntry("Spectate VR Ability", "Deadzone applied to the right stick before changing the selected slot.", Min = 0f, Max = 0.7f, HostControlled = false)]
        public static float AbilitySelectionDeadzone = 0.25f;

        [FeatureConfigEntry("Spectate VR Ability", "Enable verbose logging for the vanilla ability bridge (rate-limited).", HostControlled = false)]
        public static bool DebugAbility = false;

        [FeatureConfigEntry("Spectate Movement", "Hand used to hold the grip that controls camera look/movement while spectating. Valid values: Auto (opposite the selected main hand in RepoXR), Left, Right.", Options = new[] { "Auto", "Left", "Right" }, HostControlled = false)]
        public static string CameraGripPreference = "Auto";

        [FeatureConfigEntry("Spectate Movement", "When true, scale the jump direction by the analog stick magnitude instead of always normalizing to 1.", HostControlled = false)]
        public static bool UseAnalogMagnitude = true;

        // Master switch for the spectate -> vanilla ability input bridge.
        public static bool EnableVanillaAbilityBridge = true;
        // Enables extra logs for spectate movement guard state transitions and gating.
        public static bool DebugSpectateGuard = false;
        // Enables verbose logs for movement direction source/resolution decisions.
        public static bool DebugMovementDirection = false;
        // Logs raw controller orientation samples used during movement/aim calculations.
        public static bool LogControllerOrientation = false;
        // Enables logs related to head-camera alignment and correction behavior.
        public static bool DebugHeadAlignment = false;
    }
}
