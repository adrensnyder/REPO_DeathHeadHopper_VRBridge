#nullable enable

namespace DeathHeadHopperVRBridge.Modules.Config
{
    internal static class FeatureFlags
    {
        [FeatureConfigEntry("Spectate Movement", "Defines the forward vector used to rotate jump direction; Controller/Head use raw planar forward, HeadRaycast/ControllerRaycast use first hit from POV then head->hit projected on the horizontal plane.", AcceptableValues = new[] { "Controller", "Head", "HeadRaycast", "ControllerRaycast" })]
        public static string MovementDirectionSource = "ControllerRaycast";

        [FeatureConfigEntry("Spectate Movement", "Maximum distance (meters) for POV raycasts used by HeadRaycast/ControllerRaycast movement sources.", Min = 2f, Max = 200f)]
        public static float MovementRaycastDistance = 40f;

        [FeatureConfigEntry("Spectate Movement", "Render the RepoXR ray line for ControllerRaycast while spectating the head.")]
        public static bool ShowControllerRayLine = true;

        [FeatureConfigEntry("Spectate Movement", "When using ControllerRaycast, ignore small horizontal stick drift while pushing forward/backward.", Min = 0f, Max = 0.5f)]
        public static float ControllerRaycastXAxisDeadzone = 0.2f;

        [FeatureConfigEntry("Spectate Camera", "Distance that the spectator camera stays behind the death head.", Min = 0.05f, Max = 1.5f)]
        public static float HeadCameraDistance = 0.45f;

        [FeatureConfigEntry("Spectate Camera", "Vertical offset applied to the death head when aligning the camera.", Min = -0.2f, Max = 1.5f)]
        public static float HeadCameraHeightOffset = 0.5f;

        [FeatureConfigEntry("Spectate Camera", "How fast the spectator camera lerps back to the default position.", Min = 1f, Max = 12f)]
        public static float HeadCameraLerpSpeed = 8f;

        [FeatureConfigEntry("Spectate VR Ability", "Defines how DeathHeadHopper abilities are aimed: Controller/Head use raw forward, HeadRaycast/ControllerRaycast cast from POV to first hit and then aim from head to that point.", AcceptableValues = new[] { "Controller", "Head", "HeadRaycast", "ControllerRaycast" })]
        public static string AbilityDirectionSource = "ControllerRaycast";

        [FeatureConfigEntry("Spectate VR Ability", "Maximum distance (meters) for the POV raycast used by HeadRaycast/ControllerRaycast.", Min = 2f, Max = 200f)]
        public static float AbilityRaycastDistance = 60f;

        [FeatureConfigEntry("Spectate VR Ability", "When true, POV raycast direction is flattened to the horizon plane before tracing.")]
        public static bool AbilityRaycastUseHorizon = false;

        [FeatureConfigEntry("Spectate VR Ability", "Hand used to hold the grip that activates the spectate ability cursor. Valid values: Auto (opposite the selected main hand in RepoXR), Left, Right.", AcceptableValues = new[] { "Auto", "Left", "Right" })]
        public static string AbilityGripPreference = "Auto";

        [FeatureConfigEntry("Spectate VR Ability", "Action name (or semicolon-separated fallback names) used to activate the selected slot.")]
        public static string AbilityActivateAction = "VR Actions/ResetHeight";

        [FeatureConfigEntry("Spectate VR Ability", "Distance in meters at which the VR ability cursor floats in front of the view.", Min = 0.2f, Max = 2f)]
        public static float AbilityCursorDistance = 0.6f;

        [FeatureConfigEntry("Spectate VR Ability", "Vertical offset applied to the ability cursor relative to the view origin.", Min = -1f, Max = 1f)]
        public static float AbilityCursorVerticalOffset = -0.25f;

        [FeatureConfigEntry("Spectate VR Ability", "Horizontal spacing between the ability markers.", Min = 0.1f, Max = 1f)]
        public static float AbilityCursorSpacing = 0.35f;

        [FeatureConfigEntry("Spectate VR Ability", "Base scale applied to the ability markers.", Min = 0.04f, Max = 0.5f)]
        public static float AbilityCursorScale = 0.15f;

        [FeatureConfigEntry("Spectate VR Ability", "Deadzone applied to the right stick before changing the selected slot.", Min = 0f, Max = 0.7f)]
        public static float AbilitySelectionDeadzone = 0.25f;

        [FeatureConfigEntry("Spectate VR Ability", "Enable verbose logging for the vanilla ability bridge (rate-limited).")]
        public static bool DebugAbility = false;

        public static bool EnableVanillaAbilityBridge = true;
        public static bool DebugSpectateGuard = false;
        public static bool DebugMovementDirection = false;
        public static bool LogControllerOrientation = false;
        public static bool DebugHeadAlignment = false;

        [FeatureConfigEntry("Spectate Movement", "Hand used to hold the grip that controls camera look/movement while spectating. Valid values: Auto (opposite the selected main hand in RepoXR), Left, Right.", AcceptableValues = new[] { "Auto", "Left", "Right" })]
        public static string CameraGripPreference = "Auto";

        [FeatureConfigEntry("Spectate Movement", "When true, scale the jump direction by the analog stick magnitude instead of always normalizing to 1.")]
        public static bool UseAnalogMagnitude = true;
    }
}
