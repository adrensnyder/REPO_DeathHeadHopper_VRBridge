#nullable enable

using System;
using System.Reflection;
using BepInEx.Logging;
using DeathHeadHopper.Managers;
using DeathHeadHopperVRBridge.Modules.Config;
using DeathHeadHopperVRBridge.Modules.Logging;
using HarmonyLib;
using RepoXR.Input;
using RepoXR.Managers;
using UnityEngine;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    /// <summary>Ensures DHHInputManager uses the aligned spectator camera direction while dead.</summary>
    internal static class DHHInputBridge
    {
        private static readonly FieldInfo? TMainCameraField =
            AccessTools.Field(typeof(DHHInputManager), "_tMainCamera");
        internal static readonly ManualLogSource Log = MovementAnalog.Log;
        internal const string ModuleTag = MovementAnalog.ModuleTag;
        internal static MovementDirectionSource MovementDirection => ParseMovementDirection();
        internal static AbilityDirectionSource AbilityDirection => ParseAbilityDirection();
        private static Transform? _abilityAimProxy;

        internal static void UpdateCameraReference(DHHInputManager instance)
        {
            if (instance == null)
            {
                return;
            }

            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return;
            }

            var cameraTransform = SpectateHeadBridge.GetHmdCameraTransform()
                                  ?? SpectateHeadBridge.GetAlignedCameraTransform();
            if (cameraTransform == null || TMainCameraField == null)
            {
                return;
            }

            var current = TMainCameraField.GetValue(instance) as Transform;
            if (current == cameraTransform)
            {
                return;
            }

            TMainCameraField.SetValue(instance, cameraTransform);
            if (FeatureFlags.DebugHeadAlignment && LogLimiter.Allow("UpdateCameraReference", 1f))
            {
                Log.LogInfo($"{ModuleTag} UpdateCameraReference swapped camera transform");
            }
        }

        internal static bool TryGetAnalogMovement(out Vector2 movement)
        {
            var analogAvailable = MovementAnalog.TryGetAnalog(out movement);
            if (FeatureFlags.DebugHeadAlignment && LogLimiter.Allow("AnalogMovementCheck", 0.5f))
            {
                Log.LogInfo($"{ModuleTag} TryGetAnalogMovement analogAvailable={analogAvailable} movement={movement:F3}");
            }
            return analogAvailable;
        }

        internal static Vector2 GetLegacyMovement()
        {
            return new Vector2(SemiFunc.InputMovementX(), SemiFunc.InputMovementY());
        }

        internal static Vector3 CalculateMoveDirection(Vector2 input)
        {
            var forward = GetMovementForward(input);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            var right = Vector3.Cross(Vector3.up, forward);
            var direction = forward * input.y + right * input.x;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            if (FeatureFlags.DebugMovementDirection && LogLimiter.Allow("MovementDirectionDebug", 0.5f))
            {
                Log.LogInfo($"{ModuleTag} DebugMovement direction={direction:F3} forward={forward:F3} input={input:F3} source={MovementDirection}");
            }

            if (FeatureFlags.UseAnalogMagnitude)
            {
                return Vector3.ClampMagnitude(direction, 1f);
            }

            return direction.normalized;
        }

        internal static void LogMovement(Vector2 selected, Vector2 legacy, bool analogUsed, Vector3 direction, MovementDirectionSource source)
        {
            if (!FeatureFlags.DebugMovementDirection || !LogLimiter.Allow("MovementInput", 0.5f))
            {
                return;
            }

            Log.LogDebug($"{ModuleTag} Movement selected={selected:F3} legacy={legacy:F3} analogUsed={analogUsed} forward={direction:F3} source={source}");
        }

        private static void DebugTrace(string key, string message)
        {
            if (!FeatureFlags.DebugHeadAlignment || !LogLimiter.Allow(key, 0.5f))
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        private static Vector3 GetMovementForward(Vector2 input)
        {
            var forward = MovementDirection switch
            {
                MovementDirectionSource.Head => SpectateHeadBridge.GetAlignedForward(),
                _ => GetControllerForward(input),
            };

            if (forward.sqrMagnitude < 0.0001f)
            {
                return SpectateHeadBridge.GetBaseForward();
            }

            return forward;
        }

        internal static Vector3 GetControllerForward(Vector2 movementInput)
        {
            try
            {
                var primaryTransform = TryGetPreferredControllerTransform(GetCameraGripSelection(), out var handName);
                if (primaryTransform != null)
                {
                    var forward = Vector3.ProjectOnPlane(primaryTransform.forward, Vector3.up);
                    LogControllerOrientation(primaryTransform, handName, movementInput, forward);
                    if (forward.sqrMagnitude > 1e-06f)
                    {
                        return forward.normalized;
                    }
                }

                var tracking = TrackingInput.Instance;
                var fallbackTransform = tracking?.HeadTransform;
                if (fallbackTransform != null)
                {
                    var forward = Vector3.ProjectOnPlane(fallbackTransform.forward, Vector3.up);
                    if (forward.sqrMagnitude > 0.0001f)
                    {
                        return forward.normalized;
                    }
                }
            }
            catch
            {
                // ignore tracking failures
            }

            return SpectateHeadBridge.GetBaseForward();
        }

        internal static Vector3 GetAbilityForward()
        {
            Vector3 forward = AbilityDirection switch
            {
                AbilityDirectionSource.Head => GetHeadAimForward(),
                _ => GetControllerAimForward(),
            };

            if (forward.sqrMagnitude < 0.0001f)
            {
                return SpectateHeadBridge.GetAlignedForward();
            }

            return forward.normalized;
        }

        private static Vector3 GetHeadAimForward()
        {
            var cameraTransform = SpectateHeadBridge.GetHmdCameraTransform()
                                  ?? SpectateHeadBridge.GetAlignedCameraTransform();
            if (cameraTransform != null)
            {
                return cameraTransform.forward;
            }

            return SpectateHeadBridge.GetAlignedForward();
        }

        private static Vector3 GetControllerAimForward()
        {
            try
            {
                var primaryTransform = TryGetPreferredControllerTransform(GetAbilityGripSelection(), out _);
                if (primaryTransform != null)
                {
                    return primaryTransform.forward;
                }

                var tracking = TrackingInput.Instance;
                var fallbackTransform = tracking?.HeadTransform;
                if (fallbackTransform != null)
                {
                    return fallbackTransform.forward;
                }
            }
            catch
            {
                // ignore tracking failures
            }

            return SpectateHeadBridge.GetAlignedForward();
        }

        private static GripSelection GetCameraGripSelection()
        {
            return GripSelectionHelper.Parse(FeatureFlags.CameraGripPreference);
        }

        private static GripSelection GetAbilityGripSelection()
        {
            return GripSelectionHelper.Parse(FeatureFlags.AbilityGripPreference);
        }

        private static Transform? TryGetPreferredControllerTransform(GripSelection preference, out string handName)
        {
            var track = TrackingInput.Instance;
            var useLeftGrip = GripSelectionHelper.ShouldUseLeft(preference);
            handName = useLeftGrip ? "Left" : "Right";

            Transform? trackedHand = useLeftGrip ? track?.LeftHandTransform : track?.RightHandTransform;
            if (trackedHand != null)
            {
                return trackedHand;
            }

            var session = VRSession.Instance;
            var player = session?.Player;
            if (player != null)
            {
                var secondary = player.SecondaryHand;
                if (secondary != null)
                {
                    handName = useLeftGrip ? "Left" : "Right";
                    return secondary;
                }
            }

            handName = "unknown";
            return null;
        }

        private static void LogControllerOrientation(Transform transform, string handName, Vector2 movementInput, Vector3 planarForward)
        {
            if (!FeatureFlags.LogControllerOrientation || movementInput.sqrMagnitude < 0.0001f)
            {
                return;
            }

            if (!LogLimiter.Allow("ControllerOrientation", 0.5f))
            {
                return;
            }

            var forward = transform.forward;
            var euler = transform.rotation.eulerAngles;
            DHHInputBridge.Log.LogInfo($"{ModuleTag} Controller orientation {handName} forward={forward:F3} planar={planarForward:F3} euler={euler:F3} input={movementInput:F3}");
        }

        private static MovementDirectionSource ParseMovementDirection()
        {
            var source = FeatureFlags.MovementDirectionSource;
            if (Enum.TryParse<MovementDirectionSource>(source, true, out var parsed))
            {
                return parsed;
            }

            return MovementDirectionSource.Controller;
        }

        private static AbilityDirectionSource ParseAbilityDirection()
        {
            var source = FeatureFlags.AbilityDirectionSource;
            if (Enum.TryParse<AbilityDirectionSource>(source, true, out var parsed))
            {
                return parsed;
            }

            return AbilityDirectionSource.Head;
        }

        internal enum MovementDirectionSource
        {
            Controller,
            Head
        }

        internal enum AbilityDirectionSource
        {
            Controller,
            Head
        }

        internal static bool TrySwapAimCamera(DHHInputManager instance, out Transform? original)
        {
            original = null;
            if (TMainCameraField == null || instance == null)
            {
                return false;
            }

            var forward = GetAbilityForward();
            if (forward.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            var current = TMainCameraField.GetValue(instance) as Transform;
            if (current == null)
            {
                return false;
            }

            if (_abilityAimProxy == null)
            {
                var proxy = new GameObject("DeathHeadHopperVRBridge_AbilityAimProxy");
                UnityEngine.Object.DontDestroyOnLoad(proxy);
                _abilityAimProxy = proxy.transform;
            }

            original = current;
            _abilityAimProxy.position = current.position;
            _abilityAimProxy.rotation = Quaternion.LookRotation(forward, Vector3.up);
            TMainCameraField.SetValue(instance, _abilityAimProxy);
            return true;
        }

        internal static void RestoreAimCamera(DHHInputManager instance, Transform original)
        {
            if (TMainCameraField == null || instance == null || original == null)
            {
                return;
            }

            TMainCameraField.SetValue(instance, original);
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "Update")]
    internal static class DHHInputManagerUpdatePatch
    {
        static void Prefix(DHHInputManager __instance)
        {
            if (FeatureFlags.DebugHeadAlignment && LogLimiter.Allow("DHHUpdatePatch", 1f))
            {
                DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} DHHInputManager.Update patch running");
            }
            DHHInputBridge.UpdateCameraReference(__instance);
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "LateUpdate")]
    internal static class DHHInputManagerLateUpdatePatch
    {
        static void Prefix(DHHInputManager __instance)
        {
            DHHInputBridge.UpdateCameraReference(__instance);
            var spectating = SpectateHeadBridge.IsSpectatingHead();
            if (FeatureFlags.DebugHeadAlignment && LogLimiter.Allow("LateUpdateSpectate", 2))
            {
                DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} LateUpdate running spectatingHead={spectating}");
            }
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "GetMoveDirection")]
    internal static class DHHInputManagerGetMoveDirectionPatch
    {
        static bool Prefix(ref Vector3 __result)
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                if (FeatureFlags.DebugSpectateGuard && LogLimiter.Allow("MovementGuard", 2))
                {
                    DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} Movement guard skipped (not spectating head)");
                }
                return true;
            }

            Vector2 movement;
            var analogAvailable = DHHInputBridge.TryGetAnalogMovement(out movement);
            var legacy = DHHInputBridge.GetLegacyMovement();
            if (!analogAvailable)
            {
                movement = legacy;
            }

            var direction = DHHInputBridge.CalculateMoveDirection(movement);
            if (FeatureFlags.DebugMovementDirection && LogLimiter.Allow("MovementInputs", 1))
            {
                DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} Movement patch triggered analog={analogAvailable} input={movement:F3} direction={direction:F3} source={DHHInputBridge.MovementDirection}");
            }
            if (!analogAvailable && FeatureFlags.DebugMovementDirection && LogLimiter.Allow("MovementFallback", 1))
            {
                DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} Falling back to legacy WASD input {legacy:F3}");
            }
            DHHInputBridge.LogMovement(movement, legacy, analogAvailable, direction, DHHInputBridge.MovementDirection);
            __result = direction;
            return false;
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "ChargeWindup")]
    internal static class DHHInputManagerChargeWindupPatch
    {
        static void Prefix(DHHInputManager __instance, ref Transform? __state)
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return;
            }

            if (DHHInputBridge.TrySwapAimCamera(__instance, out var original))
            {
                __state = original;
            }
        }

        static void Postfix(DHHInputManager __instance, Transform? __state)
        {
            if (__state != null)
            {
                DHHInputBridge.RestoreAimCamera(__instance, __state);
            }
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "UpdateChargeWindup")]
    internal static class DHHInputManagerUpdateChargeWindupPatch
    {
        static void Prefix(DHHInputManager __instance, ref Transform? __state)
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return;
            }

            if (DHHInputBridge.TrySwapAimCamera(__instance, out var original))
            {
                __state = original;
            }
        }

        static void Postfix(DHHInputManager __instance, Transform? __state)
        {
            if (__state != null)
            {
                DHHInputBridge.RestoreAimCamera(__instance, __state);
            }
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputMovementX")]
    internal static class SemiFuncInputMovementXPatch
    {
        static bool Prefix(ref float __result)
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return true;
            }

            Vector2 analog;
            if (!MovementAnalog.TryGetAnalog(out analog))
            {
                return true;
            }

            MovementAnalog.LogAnalog(analog, true);
            __result = analog.x;
            return false;
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputMovementY")]
    internal static class SemiFuncInputMovementYPatch
    {
        static bool Prefix(ref float __result)
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return true;
            }

            Vector2 analog;
            if (!MovementAnalog.TryGetAnalog(out analog))
            {
                return true;
            }

            __result = analog.y;
            return false;
        }
    }
}
