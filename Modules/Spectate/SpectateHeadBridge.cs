#nullable enable

using System;
using System.Reflection;
using BepInEx.Logging;
using DeathHeadHopper.Managers;
using DeathHeadHopper.DeathHead;
using DeathHeadHopperVRBridge.Modules.Config;
using DeathHeadHopperVRBridge.Modules.Logging;
using HarmonyLib;
using Logger = BepInEx.Logging.Logger;
using UnityEngine;
using RepoXR.Managers;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    /// <summary>Aligns the spectate camera with the death head while the local player is spectating.</summary>
    internal static class SpectateHeadBridge
    {
        internal static readonly ManualLogSource Log = Logger.CreateLogSource("DeathHeadHopperFix-VR.HeadBridge");
        internal const string ModuleTag = "[DeathHeadHopperFix-VR] [SpectateCam]";
        private static readonly FieldInfo? SpectateCameraMainCameraField =
            AccessTools.Field(typeof(SpectateCamera), "MainCamera");
        private static readonly Type? ActionsType = AccessTools.TypeByName("RepoXR.Input.Actions");
        private static readonly PropertyInfo? InstanceProperty =
            ActionsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo? IndexerProperty =
            ActionsType?.GetProperty("Item", new[] { typeof(string) });
        private static readonly FieldInfo? PlayerDeathHeadGrabField =
            AccessTools.Field(typeof(PlayerDeathHead), "physGrabObject");
        private static readonly FieldInfo? PlayerDeathHeadTriggeredField =
            AccessTools.Field(typeof(PlayerDeathHead), "triggered");
        private static Vector3 _baseForward = Vector3.forward;
        private static bool _baseForwardSet;
        private static readonly string[] LeftGripBindings = { "GripLeft", "leftGrip", "Grab", "MapGrabLeft" };
        private static readonly string[] RightGripBindings = { "GripRight", "rightGrip", "Grab", "MapGrabRight" };
        internal static readonly FieldInfo? DeathHeadSpectatedField =
            AccessTools.Field(typeof(DeathHeadController), "spectated");
        private static bool _spectatedAnchorActive;
        internal static bool VrModeActive => VRSession.InVR;
        private static object? ActionsInstance => InstanceProperty?.GetValue(null);

        /// <summary>Positions and rotates the spectator camera behind the death head.</summary>
        internal static void AlignHeadCamera(bool force)
        {
            if (!VrModeActive)
            {
                return;
            }

            TraceAlignment($"AlignHeadCamera start force={force}");
            LogImmediate($"AlignHeadCamera immediate start force={force}");
            LogImmediate($"AlignHeadCamera immediate log force={force}");
            var spectate = SpectateCamera.instance;
            var playerAvatar = PlayerAvatar.instance;
            var head = playerAvatar?.playerDeathHead;
            var mainCamera = GetSpectateMainCamera(spectate);
            if (spectate == null || head == null || mainCamera == null)
            {
                TraceAlignment("AlignHeadCamera aborted: missing context or camera");
                return;
            }

            var gripPressed = IsGripPressed();
            if (InternalDebugConfig.DebugHeadAlignment && LogLimiter.Allow(nameof(AlignHeadCamera), 0.25f))
            {
                Log.LogDebug($"{ModuleTag} AlignHeadCamera force={force} gripPressed={gripPressed}");
            }
            if (!force && gripPressed)
            {
                if (InternalDebugConfig.DebugHeadAlignment && LogLimiter.Allow("HeadAlignmentGuard", 0.5f))
                {
                    Log.LogInfo($"{ModuleTag} alignment skipped because grip is pressed");
                }
                return;
            }

            var physGrabObject = PlayerDeathHeadGrabField?.GetValue(head) as PhysGrabObject;
            var basePoint = physGrabObject?.centerPoint ?? head.transform.position;
            var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f && playerAvatar != null)
            {
                forward = Vector3.ProjectOnPlane(playerAvatar.transform.forward, Vector3.up);
            }

            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            if (force || !_baseForwardSet)
            {
                _baseForward = forward;
                _baseForwardSet = true;
            }
            var distance = FeatureFlags.HeadCameraDistance;
            var height = FeatureFlags.HeadCameraHeightOffset;
            var desiredPosition = basePoint + Vector3.up * height - forward * distance;
            var desiredRotation = Quaternion.LookRotation(forward, Vector3.up);
            var lerpSpeed = FeatureFlags.HeadCameraLerpSpeed;
            var t = Mathf.Clamp01(lerpSpeed * Time.deltaTime);

            spectate.transform.position = Vector3.Lerp(spectate.transform.position, desiredPosition, t);
            spectate.transform.rotation = Quaternion.Slerp(spectate.transform.rotation, desiredRotation, t);
            mainCamera.transform.position = spectate.transform.position;
            mainCamera.transform.rotation = spectate.transform.rotation;
        }

        internal static bool IsGripPressed()
        {
            return IsGripPressedForCamera();
        }

        internal static bool IsGripPressedForCamera()
        {
            var preference = GripSelectionHelper.Parse(FeatureFlags.CameraGripPreference);
            return IsGripPressed(preference);
        }

        internal static bool IsGripPressedForAbility()
        {
            var preference = GripSelectionHelper.Parse(FeatureFlags.AbilityGripPreference);
            return IsGripPressed(preference);
        }

        private static bool IsGripPressed(GripSelection selection)
        {
            var useLeftGrip = GripSelectionHelper.ShouldUseLeft(selection);
            var bindings = useLeftGrip ? LeftGripBindings : RightGripBindings;

            foreach (var binding in bindings)
            {
                var action = GetAction(binding);
                if (IsActionPressed(action))
                {
                    if (InternalDebugConfig.DebugHeadAlignment && LogLimiter.Allow(nameof(IsGripPressed), 0.5f))
                    {
                        Log.LogDebug($"{ModuleTag} Grip detected on binding {binding} (leftGrip={useLeftGrip}) using {selection}");
                    }

                    return true;
                }
            }

            return false;
        }

        private static Camera? GetSpectateMainCamera(SpectateCamera? spectate)
        {
            if (spectate == null || SpectateCameraMainCameraField == null)
            {
                return null;
            }

            return SpectateCameraMainCameraField.GetValue(spectate) as Camera;
        }

        internal static Vector3 GetBaseForward()
        {
            return _baseForwardSet ? _baseForward : GetAlignedForward();
        }

        internal static bool IsSpectatingHead()
        {
            if (!VrModeActive)
            {
                return false;
            }

            var instance = SpectateCamera.instance;
            if (instance != null && instance.CheckState(SpectateCamera.State.Head))
            {
                TraceAlignment("SpectateCamera reports State.Head");
                return true;
            }

            var playerHead = PlayerAvatar.instance?.playerDeathHead;
            if (playerHead != null)
            {
                DeathHeadController? controller = null;
                if (playerHead.TryGetComponent<DeathHeadController>(out var tempController))
                {
                    controller = tempController;
                }

                if (controller != null && DeathHeadSpectatedField != null)
                {
                    if (DeathHeadSpectatedField.GetValue(controller) is bool spectated)
                    {
                        TraceAlignment($"DeathHeadController.spectated={spectated}");
                        if (spectated)
                        {
                            return true;
                        }
                    }
                }

                // fallback: check if overrideSpectated flag is true via reflection
                var overrideField = AccessTools.Field(typeof(PlayerDeathHead), "overrideSpectated");
                if (overrideField != null && overrideField.GetValue(playerHead) is bool overrideSpectated && overrideSpectated)
                {
                    TraceAlignment("PlayerDeathHead.overrideSpectated true");
                    return true;
                }
            }

            return false;
        }

        internal static bool IsDhhSpectateContextActive()
        {
            if (!VrModeActive)
            {
                return false;
            }

            if (DHHInputManager.instance == null)
            {
                return false;
            }

            var localHead = PlayerAvatar.instance?.playerDeathHead;
            if (localHead == null || PlayerDeathHeadTriggeredField == null)
            {
                return false;
            }

            return PlayerDeathHeadTriggeredField.GetValue(localHead) as bool? ?? false;
        }

        internal static bool IsLocalDeathHeadTriggered()
        {
            if (!VrModeActive || DHHInputManager.instance == null)
            {
                return false;
            }

            var localHead = PlayerAvatar.instance?.playerDeathHead;
            if (localHead == null || PlayerDeathHeadTriggeredField == null)
            {
                return false;
            }

            return PlayerDeathHeadTriggeredField.GetValue(localHead) as bool? ?? false;
        }

        internal static bool IsDhhRuntimeInputContextActive()
        {
            if (!VrModeActive || DHHInputManager.instance == null)
            {
                return false;
            }

            // Primary path: explicit DHH runtime state on local death-head.
            if (IsDhhSpectateContextActive())
            {
                return true;
            }

            // Fallback path: DHH input manager is active and we are in head spectate context.
            return IsSpectatingHead();
        }

        internal static Transform? GetAlignedCameraTransform()
        {
            var spectate = SpectateCamera.instance;
            var mainCamera = GetSpectateMainCamera(spectate);
            if (mainCamera != null)
            {
                return mainCamera.transform;
            }

            return spectate?.transform;
        }

        internal static Vector3 GetAlignedForward()
        {
            var cameraTransform = GetAlignedCameraTransform();
            if (cameraTransform == null)
            {
                return Vector3.forward;
            }

            var forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = cameraTransform.forward;
                forward.y = 0f;
            }

            return forward.normalized;
        }

        private static object? GetAction(string name)
        {
            var instance = ActionsInstance;
            if (instance == null || IndexerProperty == null)
            {
                return null;
            }

            try
            {
                return IndexerProperty.GetValue(instance, new object[] { name });
            }
            catch
            {
                return null;
            }
        }

        private static bool IsActionPressed(object? action)
        {
            if (action == null)
            {
                return false;
            }

            var method = action.GetType().GetMethod("IsPressed", Type.EmptyTypes);
            if (method == null)
            {
                return false;
            }

            try
            {
                var result = method.Invoke(action, null);
                return result is bool pressed && pressed;
            }
            catch
            {
                return false;
            }
        }

        internal static Transform? GetHmdCameraTransform()
        {
            if (!VrModeActive || VRSession.Instance == null)
            {
                return null;
            }

            return VRSession.Instance.MainCamera?.transform;
        }

        internal static void TraceAlignment(string message)
        {
            if (!InternalDebugConfig.DebugHeadAlignment || !LogLimiter.Allow("AlignTrace", 0.5f))
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        internal static void LogImmediate(string message)
        {
            if (!InternalDebugConfig.DebugHeadAlignment)
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        private static void LogAnchorCameraState(string message, SpectateCamera? spectate)
        {
            if (!InternalDebugConfig.DebugHeadAlignment)
            {
                return;
            }

            var mainCamera = GetSpectateMainCamera(spectate);
            var forward = mainCamera?.transform.forward ?? spectate?.transform.forward ?? Vector3.zero;
            var position = mainCamera?.transform.position ?? spectate?.transform.position ?? Vector3.zero;
            Log.LogInfo($"{ModuleTag} {message} pos={position:F3} forward={forward:F3}");
        }

        internal static void OnSpectatedAnchor(bool value)
        {
            if (value
                && !_spectatedAnchorActive)
            {
                _spectatedAnchorActive = true;
                TraceAlignment("Spectated anchor activated");
                LogImmediate("Spectated anchor activated");
                AlignHeadCamera(true);
                LogAnchorCameraState("Spectated anchor camera", SpectateCamera.instance);
                return;
            }

            if (!value && _spectatedAnchorActive)
            {
                _spectatedAnchorActive = false;
                TraceAlignment("Spectated anchor cleared");
                LogImmediate("Spectated anchor cleared");
            }
        }

    }

    [HarmonyPatch(typeof(SpectateCamera), "StateHead")]
    internal static class SpectateCameraStateHeadPatch
    {
        static void Postfix()
        {
            SpectateHeadBridge.TraceAlignment("SpectateCamera.StateHead Postfix triggered");
            SpectateHeadBridge.LogImmediate("SpectateCamera.StateHead Postfix triggered");
            if (InternalDebugConfig.DebugHeadAlignment && LogLimiter.Allow("StateHead.Enter", 0.5f))
            {
                SpectateHeadBridge.Log.LogDebug($"{SpectateHeadBridge.ModuleTag} SpectateCamera.StateHead entered, queuing head alignment");
            }
            SpectateHeadBridge.AlignHeadCamera(false);
        }
    }

    [HarmonyPatch(typeof(SpectateCamera), "UpdateState")]
    internal static class SpectateCameraUpdateStatePatch
    {
        private static SpectateCamera.State _previousState = SpectateCamera.State.Death;

        static void Postfix(SpectateCamera.State _state)
        {
            _previousState = _state;
            if (_state == SpectateCamera.State.Head)
            {
                SpectateHeadBridge.AlignHeadCamera(false);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerDeathHead), "OverrideSpectatedReset")]
    internal static class PlayerDeathHeadResetPatch
    {
        static void Postfix()
        {
            SpectateHeadBridge.TraceAlignment("PlayerDeathHead.OverrideSpectatedReset Postfix triggered");
            SpectateHeadBridge.LogImmediate("PlayerDeathHead.OverrideSpectatedReset Postfix triggered");
        }
}

    [HarmonyPatch(typeof(DeathHeadController), "SetSpectated")]
    internal static class DeathHeadControllerSetSpectatedPatch
    {
    static void Postfix(DeathHeadController __instance, bool value)
    {
        SpectateHeadBridge.LogImmediate($"DeathHeadController.SetSpectated value={value}");
        SpectateHeadBridge.OnSpectatedAnchor(value);
    }
    }

    [HarmonyPatch(typeof(PlayerDeathHead), "SpectatedSet")]
    internal static class PlayerDeathHeadSpectatedSetPatch
    {
    static void Postfix(PlayerDeathHead __instance, bool _active)
    {
        SpectateHeadBridge.LogImmediate($"PlayerDeathHead.SpectatedSet _active={_active}");
        if (_active)
        {
            SpectateHeadBridge.OnSpectatedAnchor(true);
        }
    }
    }

    [HarmonyPatch]
    internal static class HeadCameraTurnGripGuard
    {
        private static readonly Type? SpectatePatchesType = AccessTools.TypeByName("RepoXR.Patches.SpectatePatches");

        static MethodBase? TargetMethod()
        {
            if (SpectatePatchesType == null)
            {
                return null;
            }

            return AccessTools.Method(SpectatePatchesType, "HeadCameraTurnPatch");
        }

        static bool Prefix()
        {
            var grip = SpectateHeadBridge.IsGripPressedForCamera();
            if (InternalDebugConfig.DebugHeadAlignment && LogLimiter.Allow("HeadCameraTurnGuard", 0.5f))
            {
                SpectateHeadBridge.Log.LogDebug($"{SpectateHeadBridge.ModuleTag} HeadCameraTurnPatch guard gripPressed={grip}");
            }
            return grip;
        }
    }
}
