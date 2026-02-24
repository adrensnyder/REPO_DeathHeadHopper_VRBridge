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
using RepoXR.UI;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal static class DHHInputBridge
    {
        private static readonly FieldInfo? TMainCameraField = AccessTools.Field(typeof(DHHInputManager), "_tMainCamera");
        private static readonly FieldInfo? RepoXRLeftInteractorField = AccessTools.Field(typeof(XRRayInteractorManager), "leftInteractor");
        private static readonly FieldInfo? RepoXRRightInteractorField = AccessTools.Field(typeof(XRRayInteractorManager), "rightInteractor");
        private static readonly FieldInfo? JumpBufferTimerField = AccessTools.Field(typeof(DeathHeadHopper.DeathHead.Handlers.JumpHandler), "jumpBufferTimer");
        private static readonly FieldInfo? JumpCooldownTimerField = AccessTools.Field(typeof(DeathHeadHopper.DeathHead.Handlers.JumpHandler), "jumpCooldownTimer");

        internal static readonly ManualLogSource Log = MovementAnalog.Log;
        internal const string ModuleTag = MovementAnalog.ModuleTag;
        internal static MovementDirectionSource MovementDirection => ParseMovementDirection();

        private static Transform? _abilityAimProxy;
        private static bool _repoXRRayVisible;
        private static bool _repoXRRayLengthOverridden;
        private static Transform? _repoXRRayHitMarker;

        internal static void UpdateCameraReference(DHHInputManager instance)
        {
            if (instance == null || !SpectateHeadBridge.IsDhhRuntimeInputContextActive())
            {
                return;
            }

            var cameraTransform = SpectateHeadBridge.GetHmdCameraTransform() ?? SpectateHeadBridge.GetAlignedCameraTransform();
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
        }

        internal static bool TryGetAnalogMovement(out Vector2 movement)
        {
            return MovementAnalog.TryGetAnalog(out movement);
        }

        internal static Vector2 GetLegacyMovement()
        {
            return new Vector2(SemiFunc.InputMovementX(), SemiFunc.InputMovementY());
        }

        internal static Vector3 CalculateMoveDirection(Vector2 input)
        {
            if ((MovementDirection == MovementDirectionSource.ControllerRaycast || MovementDirection == MovementDirectionSource.HeadRaycast)
                && Mathf.Abs(input.x) <= Mathf.Clamp01(FeatureFlags.ControllerRaycastXAxisDeadzone)
                && Mathf.Abs(input.y) > 0.05f)
            {
                input.x = 0f;
            }

            if (MovementDirection == MovementDirectionSource.ControllerRaycast || MovementDirection == MovementDirectionSource.HeadRaycast)
            {
                // Favor forward/back intent when vertical input clearly dominates, reducing joystick X drift.
                var absX = Mathf.Abs(input.x);
                var absY = Mathf.Abs(input.y);
                var minY = Mathf.Clamp01(FeatureFlags.ControllerRaycastForwardPriorityMinY);
                var dominanceRatio = Mathf.Clamp(FeatureFlags.ControllerRaycastForwardPriorityRatio, 1f, 3f);
                if (absY > minY && absY >= absX * dominanceRatio)
                {
                    input.x = 0f;
                }
            }

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

            return FeatureFlags.UseAnalogMagnitude ? Vector3.ClampMagnitude(direction, 1f) : direction.normalized;
        }

        internal static void LogMovement(Vector2 selected, Vector2 legacy, bool analogUsed, Vector3 direction, MovementDirectionSource source)
        {
            if (!InternalDebugConfig.DebugMovementDirection || !LogLimiter.Allow("MovementInput", 0.5f))
            {
                return;
            }

            Log.LogDebug($"{ModuleTag} Movement selected={selected:F3} legacy={legacy:F3} analogUsed={analogUsed} forward={direction:F3} source={source}");
        }

        private static Vector3 GetMovementForward(Vector2 input)
        {
            var forward = MovementDirection switch
            {
                MovementDirectionSource.HeadRaycast => GetRaycastAimForward(false, GetCameraGripSelection(), true, Mathf.Max(1f, FeatureFlags.MovementRaycastDistance), false),
                _ => GetRaycastAimForward(true, GetCameraGripSelection(), true, Mathf.Max(1f, FeatureFlags.MovementRaycastDistance), false),
            };

            return forward.sqrMagnitude < 0.0001f ? SpectateHeadBridge.GetBaseForward() : forward;
        }

        internal static Vector3 GetControllerForward(Vector2 movementInput)
        {
            try
            {
                var primaryTransform = TryGetPreferredControllerTransform(GetCameraGripSelection(), out _);
                if (primaryTransform != null)
                {
                    var forward = Vector3.ProjectOnPlane(primaryTransform.forward, Vector3.up);
                    if (forward.sqrMagnitude > 1e-06f)
                    {
                        return forward.normalized;
                    }
                }

                var fallbackTransform = TrackingInput.Instance?.HeadTransform;
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
            }

            return SpectateHeadBridge.GetBaseForward();
        }

        internal static Vector3 GetAbilityForward()
        {
            var forward = MovementDirection switch
            {
                MovementDirectionSource.HeadRaycast => GetRaycastAimForward(false, GetUnifiedRaycastGripSelection(), false, GetUnifiedRaycastDistance(), false),
                _ => GetRaycastAimForward(true, GetUnifiedRaycastGripSelection(), false, GetUnifiedRaycastDistance(), false),
            };

            return forward.sqrMagnitude < 0.0001f ? SpectateHeadBridge.GetAlignedForward() : forward.normalized;
        }

        internal static bool ShouldSuppressLegacyMovement()
        {
            // Movement suppression must also cover the DHHF fallback path that reads
            // SemiFunc.InputMovementX/Y directly while the local death-head is triggered.
            if (!SpectateHeadBridge.IsDhhRuntimeInputContextActive() && !SpectateHeadBridge.IsLocalDeathHeadTriggered())
            {
                return false;
            }

            if (!SpectateHeadBridge.IsGripPressedForCamera())
            {
                return false;
            }

            if (InternalDebugConfig.DebugSpectateGuard && LogLimiter.Allow("MovementGuard.LegacySuppressed", 0.2f))
            {
                Log.LogInfo($"{ModuleTag} Legacy movement suppressed while spectating head because camera grip is held.");
            }
            return true;
        }

        private static Vector3 GetHeadAimForward()
        {
            var cameraTransform = SpectateHeadBridge.GetHmdCameraTransform() ?? SpectateHeadBridge.GetAlignedCameraTransform();
            return cameraTransform != null ? cameraTransform.forward : SpectateHeadBridge.GetAlignedForward();
        }

        private static Vector3 GetRaycastAimForward(bool useControllerSource, GripSelection controllerPreference, bool projectToHorizontal, float maxDistance, bool useHorizonRay)
        {
            if (!TryGetRaySource(useControllerSource, controllerPreference, out var sourcePosition, out var sourceForward, out _))
            {
                DisableControllerRayVisualizer();
                return useControllerSource ? GetControllerAimForward() : GetHeadAimForward();
            }

            var rayDirection = sourceForward.normalized;
            if (useHorizonRay)
            {
                var planar = Vector3.ProjectOnPlane(rayDirection, Vector3.up);
                if (planar.sqrMagnitude > 0.0001f)
                {
                    rayDirection = planar.normalized;
                }
            }

            if (rayDirection.sqrMagnitude < 0.0001f)
            {
                DisableControllerRayVisualizer();
                return useControllerSource ? GetControllerAimForward() : GetHeadAimForward();
            }

            var ray = new Ray(sourcePosition, rayDirection);
            var head = PlayerAvatar.instance?.playerDeathHead;
            var targetPoint = ray.origin + ray.direction * maxDistance;

            if (TryGetFirstRaycastHit(ray, maxDistance, out var hit))
            {
                targetPoint = hit.point;
            }

            if (useControllerSource)
            {
                SetRepoXRRayVisibility(FeatureFlags.ShowControllerRayLine && SpectateHeadBridge.IsSpectatingHead());
            }

            // DHH-style movement adapted to raycast target:
            // treat the target point as a "virtual camera origin" and invert the usual camera-forward logic,
            // so pushing forward moves the head toward the selected target point.
            var direction = head == null
                ? ray.direction
                : GetHeadToTargetDirection(head.transform.position, targetPoint);
            if (projectToHorizontal)
            {
                direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            }

            if (direction.sqrMagnitude < 0.0001f)
            {
                return ray.direction;
            }

            return direction.normalized;
        }

        private static Vector3 GetHeadToTargetDirection(Vector3 headPosition, Vector3 targetPoint)
        {
            var direction = targetPoint - headPosition;
            return direction.sqrMagnitude < 0.0001f ? Vector3.zero : direction.normalized;
        }

        internal static void UpdateRealtimeControllerRayPreview()
        {
            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                DisableControllerRayVisualizer();
                SetRepoXRRayHitMarkerVisible(false);
                return;
            }

            var usesControllerRay = UsesAnyControllerRay();
            if (!usesControllerRay)
            {
                DisableControllerRayVisualizer();
                SetRepoXRRayHitMarkerVisible(false);
                return;
            }

            SetRepoXRRayVisibility(FeatureFlags.ShowControllerRayLine);
            UpdateRepoXRRayHitMarkerPreview();
        }

        private static void DisableControllerRayVisualizer()
        {
            SetRepoXRRayVisibility(false);
            RestoreRepoXRRayLengthOverride();
            SetRepoXRRayHitMarkerVisible(false);
        }

        private static void UpdateRepoXRRayHitMarkerPreview()
        {
            if (!FeatureFlags.ShowControllerRayHitMarker)
            {
                SetRepoXRRayHitMarkerVisible(false);
                return;
            }

            var maxDistance = Mathf.Max(FeatureFlags.ControllerRayLineLength, GetUnifiedRaycastDistance());
            if (!TryGetControllerRaycastHit(GetUnifiedRaycastGripSelection(), maxDistance, out var hit))
            {
                SetRepoXRRayHitMarkerVisible(false);
                return;
            }

            EnsureRepoXRRayHitMarker();
            if (_repoXRRayHitMarker == null)
            {
                return;
            }

            var size = Mathf.Clamp(FeatureFlags.ControllerRayHitMarkerSize, 0.005f, 0.2f);
            _repoXRRayHitMarker.localScale = Vector3.one * size;
            _repoXRRayHitMarker.position = hit.point + hit.normal * Mathf.Max(0.001f, size * 0.1f);
            _repoXRRayHitMarker.rotation = Quaternion.LookRotation(hit.normal.sqrMagnitude > 0.0001f ? hit.normal : Vector3.up);
            _repoXRRayHitMarker.gameObject.SetActive(true);
        }

        private static bool TryGetControllerRaycastHit(GripSelection controllerPreference, float maxDistance, out RaycastHit selectedHit)
        {
            selectedHit = default;
            if (!TryGetRaySource(true, controllerPreference, out var sourcePosition, out var sourceForward, out _))
            {
                return false;
            }

            var rayDirection = sourceForward.normalized;
            if (rayDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            var ray = new Ray(sourcePosition, rayDirection);
            if (!TryGetFirstRaycastHit(ray, maxDistance, out var hit))
            {
                return false;
            }

            selectedHit = hit;
            return true;
        }

        private static bool TryGetFirstRaycastHit(Ray ray, float maxDistance, out RaycastHit selectedHit)
        {
            selectedHit = default;
            var hits = Physics.RaycastAll(ray, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
            {
                return false;
            }

            Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
            selectedHit = hits[0];
            return true;
        }

        private static void EnsureRepoXRRayHitMarker()
        {
            if (_repoXRRayHitMarker != null)
            {
                return;
            }

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "DeathHeadHopperVRBridge_RepoXRRayHitMarker";
            marker.layer = 0;
            UnityEngine.Object.DontDestroyOnLoad(marker);

            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    renderer.material = new Material(shader);
                }

                renderer.material.color = Color.red;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            marker.SetActive(false);
            _repoXRRayHitMarker = marker.transform;
        }

        private static void SetRepoXRRayHitMarkerVisible(bool visible)
        {
            if (_repoXRRayHitMarker == null)
            {
                return;
            }

            _repoXRRayHitMarker.gameObject.SetActive(visible);
        }

        private static void SetRepoXRRayVisibility(bool visible)
        {
            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                return;
            }

            if (!visible)
            {
                if (_repoXRRayVisible)
                {
                    manager.SetVisible(false);
                }
                _repoXRRayVisible = false;
                return;
            }

            manager.SetVisible(true);
            if (SpectateHeadBridge.IsSpectatingHead())
            {
                ApplyRepoXRRayLengthOverride();

                var visuals = manager.GetComponentsInChildren<XRInteractorLineVisual>(true);
                for (var i = 0; i < visuals.Length; i++)
                {
                    visuals[i].enabled = false;
                }

                if (TryGetVisualizedRepoXRInteractor(out var selectedInteractor) && selectedInteractor != null)
                {
                    var selectedVisual = selectedInteractor.GetComponent<XRInteractorLineVisual>();
                    if (selectedVisual != null)
                    {
                        selectedVisual.enabled = true;
                        ApplyActiveVisualStyle(selectedVisual, Mathf.Max(1f, FeatureFlags.ControllerRayLineLength));
                    }
                }
            }

            _repoXRRayVisible = true;
        }

        private static void ApplyRepoXRRayLengthOverride()
        {
            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                return;
            }

            if (!TryGetVisualizedRepoXRInteractor(out var selectedInteractor) || selectedInteractor == null)
            {
                return;
            }

            var selectedVisual = selectedInteractor.GetComponent<XRInteractorLineVisual>();
            if (selectedVisual == null)
            {
                return;
            }

            var configuredLength = Mathf.Max(1f, FeatureFlags.ControllerRayLineLength);
            ApplyActiveVisualStyle(selectedVisual, configuredLength);

            if (TryGetNonSelectedRepoXRInteractor(selectedInteractor, out var nonSelectedInteractor) && nonSelectedInteractor != null)
            {
                var nonSelectedVisual = nonSelectedInteractor.GetComponent<XRInteractorLineVisual>();
                if (nonSelectedVisual != null)
                {
                    ApplyInactiveVisualStyle(nonSelectedVisual);
                }
            }

            _repoXRRayLengthOverridden = true;
        }

        private static void RestoreRepoXRRayLengthOverride()
        {
            if (!_repoXRRayLengthOverridden)
            {
                return;
            }

            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                _repoXRRayLengthOverridden = false;
                return;
            }

            var activeInteractor = manager.GetActiveInteractor().Item1;
            if (activeInteractor != null)
            {
                var activeVisual = activeInteractor.GetComponent<XRInteractorLineVisual>();
                if (activeVisual != null)
                {
                    ApplyActiveVisualStyle(activeVisual, 20f);
                }
            }

            if (TryGetNonSelectedRepoXRInteractor(activeInteractor, out var nonActiveInteractor) && nonActiveInteractor != null)
            {
                var nonActiveVisual = nonActiveInteractor.GetComponent<XRInteractorLineVisual>();
                if (nonActiveVisual != null)
                {
                    ApplyInactiveVisualStyle(nonActiveVisual);
                }
            }

            _repoXRRayLengthOverridden = false;
        }

        private static bool TryGetNonSelectedRepoXRInteractor(XRRayInteractor? selectedInteractor, out XRRayInteractor? otherInteractor)
        {
            otherInteractor = null;
            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                return false;
            }

            var leftInteractor = RepoXRLeftInteractorField?.GetValue(manager) as XRRayInteractor;
            var rightInteractor = RepoXRRightInteractorField?.GetValue(manager) as XRRayInteractor;

            if (selectedInteractor == null)
            {
                return false;
            }

            if (ReferenceEquals(selectedInteractor, leftInteractor))
            {
                otherInteractor = rightInteractor;
                return otherInteractor != null;
            }

            if (ReferenceEquals(selectedInteractor, rightInteractor))
            {
                otherInteractor = leftInteractor;
                return otherInteractor != null;
            }

            return false;
        }

        private static void ApplyActiveVisualStyle(XRInteractorLineVisual visual, float lineLength)
        {
            if (visual == null)
            {
                return;
            }

            visual.lineLength = Mathf.Max(1f, lineLength);
            visual.invalidColorGradient = CreateGradient(0.2f, 0.2f);
            visual.validColorGradient = CreateGradient(1f, 1f);
        }

        private static void ApplyInactiveVisualStyle(XRInteractorLineVisual visual)
        {
            if (visual == null)
            {
                return;
            }

            visual.lineLength = 1f;
            var gradient = CreateGradient(0.05f, 0f, 0.8f, 0.05f, 1f);
            visual.invalidColorGradient = gradient;
            visual.validColorGradient = gradient;
        }

        private static Gradient CreateGradient(float startAlpha, float endAlpha)
        {
            return CreateGradient(startAlpha, endAlpha, -1f, -1f, -1f);
        }

        private static Gradient CreateGradient(float startAlpha, float endAlpha, float middleTime, float middleAlpha, float finalTime)
        {
            var gradient = new Gradient();
            gradient.mode = GradientMode.Blend;

            GradientAlphaKey[] alphaKeys;
            if (middleTime >= 0f && middleAlpha >= 0f && finalTime >= 0f)
            {
                alphaKeys = new[]
                {
                    new GradientAlphaKey(startAlpha, 0f),
                    new GradientAlphaKey(middleAlpha, middleTime),
                    new GradientAlphaKey(endAlpha, finalTime)
                };
            }
            else
            {
                alphaKeys = new[]
                {
                    new GradientAlphaKey(startAlpha, 0f),
                    new GradientAlphaKey(endAlpha, 1f)
                };
            }

            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                alphaKeys);

            return gradient;
        }

        private static bool TryGetVisualizedRepoXRInteractor(out XRRayInteractor? interactor)
        {
            interactor = null;
            if (SpectateHeadBridge.IsSpectatingHead() && UsesAnyControllerRay())
            {
                // In DeathHead controller-ray mode the visualized line must follow the configured hand,
                // not RepoXR's active UI interactor state which can change on button presses.
                return TryGetRepoXRInteractor(GetUnifiedRaycastGripSelection(), out interactor);
            }

            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                return false;
            }

            interactor = manager.GetActiveInteractor().Item1;
            return interactor != null;
        }

        private static bool TryGetRaySource(bool useControllerSource, GripSelection controllerPreference, out Vector3 sourcePosition, out Vector3 sourceForward, out string sourceLabel)
        {
            sourcePosition = Vector3.zero;
            sourceForward = Vector3.forward;
            sourceLabel = "none";

            var hmd = SpectateHeadBridge.GetHmdCameraTransform() ?? SpectateHeadBridge.GetAlignedCameraTransform();
            if (!useControllerSource)
            {
                if (hmd == null)
                {
                    return false;
                }

                sourcePosition = hmd.position;
                sourceForward = hmd.forward;
                sourceLabel = "head-hmd";
                return true;
            }

            if (TryGetRepoXRControllerRay(controllerPreference, out var repoOrigin, out var repoDirection, out _, out _, out var repoLabel))
            {
                sourcePosition = repoOrigin;
                sourceForward = repoDirection;
                sourceLabel = repoLabel;
                return true;
            }

            var tracked = TryGetPreferredControllerTransform(controllerPreference, out var handName);
            if (tracked == null)
            {
                return false;
            }

            sourcePosition = tracked.position;
            sourceForward = tracked.forward;
            sourceLabel = $"tracked-{handName.ToLowerInvariant()}";
            return true;
        }

        private static bool TryGetRepoXRInteractor(GripSelection preference, out XRRayInteractor? interactor)
        {
            interactor = null;
            var manager = XRRayInteractorManager.Instance;
            if (manager == null)
            {
                return false;
            }

            var useLeft = GripSelectionHelper.ShouldUseLeft(preference);
            var preferredField = useLeft ? RepoXRLeftInteractorField : RepoXRRightInteractorField;
            interactor = preferredField?.GetValue(manager) as XRRayInteractor;
            if (interactor != null)
            {
                return true;
            }

            var fallbackField = useLeft ? RepoXRRightInteractorField : RepoXRLeftInteractorField;
            interactor = fallbackField?.GetValue(manager) as XRRayInteractor;
            if (interactor != null)
            {
                return true;
            }

            interactor = manager.GetActiveInteractor().Item1;
            return interactor != null;
        }

        private static bool TryGetRepoXRControllerRay(GripSelection preference, out Vector3 origin, out Vector3 direction, out Vector3 hitPoint, out bool hasHit, out string sourceLabel)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            hitPoint = Vector3.zero;
            hasHit = false;
            sourceLabel = "repoxr-none";

            if (!TryGetRepoXRInteractor(preference, out var interactor) || interactor == null)
            {
                return false;
            }

            if (SpectateHeadBridge.IsSpectatingHead())
            {
                interactor.raycastMask = ~0;
            }

            origin = interactor.transform.position;
            direction = interactor.transform.forward;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            direction.Normalize();
            sourceLabel = $"repoxr-{(GripSelectionHelper.ShouldUseLeft(preference) ? "left" : "right")}";

            RaycastHit hit = default;
            if (interactor.TryGetCurrent3DRaycastHit(out hit))
            {
                hitPoint = hit.point;
                hasHit = true;
            }

            return true;
        }

        private static Vector3 GetControllerAimForward()
        {
            try
            {
                var primaryTransform = TryGetPreferredControllerTransform(GetUnifiedRaycastGripSelection(), out _);
                if (primaryTransform != null)
                {
                    return primaryTransform.forward;
                }

                var fallbackTransform = TrackingInput.Instance?.HeadTransform;
                if (fallbackTransform != null)
                {
                    return fallbackTransform.forward;
                }
            }
            catch
            {
            }

            return SpectateHeadBridge.GetAlignedForward();
        }

        private static GripSelection GetCameraGripSelection() => GripSelectionHelper.Parse(FeatureFlags.CameraGripPreference);

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

            var secondary = VRSession.Instance?.Player?.SecondaryHand;
            if (secondary != null)
            {
                return secondary;
            }

            handName = "unknown";
            return null;
        }

        private static MovementDirectionSource ParseMovementDirection()
        {
            var source = (FeatureFlags.MovementDirectionSource ?? string.Empty).Trim();
            if (source.Equals("Controller", StringComparison.OrdinalIgnoreCase))
            {
                return MovementDirectionSource.ControllerRaycast;
            }

            if (source.Equals("Head", StringComparison.OrdinalIgnoreCase))
            {
                return MovementDirectionSource.HeadRaycast;
            }

            if (Enum.TryParse(source, true, out MovementDirectionSource parsed))
            {
                return parsed;
            }

            return MovementDirectionSource.ControllerRaycast;
        }

        private static GripSelection GetUnifiedRaycastGripSelection()
        {
            return GetCameraGripSelection();
        }

        private static float GetUnifiedRaycastDistance()
        {
            return Mathf.Max(1f, FeatureFlags.MovementRaycastDistance);
        }

        internal enum MovementDirectionSource
        {
            HeadRaycast,
            ControllerRaycast
        }

        private static bool UsesAnyControllerRay()
        {
            return MovementDirection == MovementDirectionSource.ControllerRaycast;
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

        internal static void ExtendJumpBufferIfNeeded(DeathHeadHopper.DeathHead.Handlers.JumpHandler jumpHandler)
        {
            if (jumpHandler == null || JumpBufferTimerField == null || JumpCooldownTimerField == null)
            {
                return;
            }

            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                return;
            }

            if (MovementDirection != MovementDirectionSource.ControllerRaycast && MovementDirection != MovementDirectionSource.HeadRaycast)
            {
                return;
            }

            var cooldownRemaining = JumpCooldownTimerField.GetValue(jumpHandler) is float cooldown ? Mathf.Max(0f, cooldown) : 0f;
            var desiredBuffer = Mathf.Max(0.35f, cooldownRemaining + 0.1f);

            if (JumpBufferTimerField.GetValue(jumpHandler) is float currentBuffer && currentBuffer < desiredBuffer)
            {
                JumpBufferTimerField.SetValue(jumpHandler, desiredBuffer);
            }
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "Update")]
    internal static class DHHInputManagerUpdatePatch
    {
        static void Prefix(DHHInputManager __instance)
        {
            DHHInputBridge.UpdateCameraReference(__instance);
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "LateUpdate")]
    internal static class DHHInputManagerLateUpdatePatch
    {
        static void Prefix(DHHInputManager __instance)
        {
            DHHInputBridge.UpdateCameraReference(__instance);
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "GetMoveDirection")]
    internal static class DHHInputManagerGetMoveDirectionPatch
    {
        static bool Prefix(ref Vector3 __result)
        {
            if (!SpectateHeadBridge.IsDhhRuntimeInputContextActive())
            {
                return true;
            }

            var analogAvailable = DHHInputBridge.TryGetAnalogMovement(out var movement);
            var legacy = DHHInputBridge.GetLegacyMovement();
            if (!analogAvailable)
            {
                movement = legacy;
            }

            __result = DHHInputBridge.CalculateMoveDirection(movement);
            DHHInputBridge.LogMovement(movement, legacy, analogAvailable, __result, DHHInputBridge.MovementDirection);
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
            if (!SpectateHeadBridge.IsDhhRuntimeInputContextActive() && !SpectateHeadBridge.IsLocalDeathHeadTriggered())
            {
                return true;
            }

            if (DHHInputBridge.ShouldSuppressLegacyMovement())
            {
                __result = 0f;
                return false;
            }

            if (!MovementAnalog.TryGetAnalog(out var analog))
            {
                return true;
            }

            __result = analog.x;
            return false;
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputMovementY")]
    internal static class SemiFuncInputMovementYPatch
    {
        static bool Prefix(ref float __result)
        {
            if (!SpectateHeadBridge.IsDhhRuntimeInputContextActive() && !SpectateHeadBridge.IsLocalDeathHeadTriggered())
            {
                return true;
            }

            if (DHHInputBridge.ShouldSuppressLegacyMovement())
            {
                __result = 0f;
                return false;
            }

            if (!MovementAnalog.TryGetAnalog(out var analog))
            {
                return true;
            }

            __result = analog.y;
            return false;
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputDown")]
    internal static class SemiFuncInputDownDirectionBindingSuppressionPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            var gripPressed = SpectateHeadBridge.IsGripPressedForAbility();
            VanillaAbilityInputBridge.NotifyDirectionBindingAttempt(key, gripPressed);
            var hasConfiguredDirectionKey = VanillaAbilityInputBridge.TryGetConfiguredDirectionInputKey(out var configuredDirectionKey);
            var suppressionDebugState = VanillaAbilityInputBridge.GetDirectionSuppressionDebugState(key);
            if (InternalDebugConfig.DebugAbilityInputFlow
                && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                && gripPressed
                && VanillaAbilityInputBridge.IsDebugBurstLoggingActive()
                && hasConfiguredDirectionKey
                && key == configuredDirectionKey
                && LogLimiter.Allow("AbilityFlow.InputDownObserved", 0.2f))
            {
                DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} InputDown observed for configured direction binding key. grip={gripPressed} {suppressionDebugState}");
            }

            if (VanillaAbilityInputBridge.ShouldSuppressDirectionBindingInputDownThisFrame(key))
            {
                if (InternalDebugConfig.DebugAbilityInputFlow
                    && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                    && LogLimiter.Allow("AbilityFlow.SuppressApplied", 0.2f))
                {
                    DHHInputBridge.Log.LogInfo($"{DHHInputBridge.ModuleTag} Suppressed SemiFunc.InputDown({key}) for direction-slot redirect.");
                }
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputHold")]
    internal static class SemiFuncInputHoldDirectionBindingSuppressionPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            if (VanillaAbilityInputBridge.ShouldSuppressDirectionBindingInputHoldThisFrame(key))
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SemiFunc), "InputUp")]
    internal static class SemiFuncInputUpDirectionBindingSuppressionPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            if (VanillaAbilityInputBridge.ShouldSuppressDirectionBindingInputUpThisFrame(key))
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SpectateCamera), "LateUpdate")]
    internal static class SpectateCameraLateUpdateControllerRayPreviewPatch
    {
        static void Postfix()
        {
            DHHInputBridge.UpdateRealtimeControllerRayPreview();
        }
    }

    [HarmonyPatch(typeof(DHHInputManager), "LateUpdate")]
    internal static class DHHInputManagerLateUpdateHeldJumpRefreshPatch
    {
        private static readonly MethodInfo? JumpMethod = AccessTools.Method(typeof(DHHInputManager), "Jump");
        private static float _nextRefreshTime;

        static void Postfix(DHHInputManager __instance)
        {
            if (__instance == null || !SpectateHeadBridge.IsDhhRuntimeInputContextActive())
            {
                return;
            }

            if (DHHInputBridge.MovementDirection != DHHInputBridge.MovementDirectionSource.ControllerRaycast &&
                DHHInputBridge.MovementDirection != DHHInputBridge.MovementDirectionSource.HeadRaycast)
            {
                return;
            }

            if (VanillaAbilityInputBridge.IsDirectionAbilityActive())
            {
                return;
            }

            // Keep updating direction while the configured direction InputKey binding is held.
            if (!VanillaAbilityInputBridge.IsDirectionBindingHeld())
            {
                return;
            }

            if (Time.time < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.time + 0.05f;
            JumpMethod?.Invoke(__instance, null);
        }
    }

    [HarmonyPatch(typeof(DeathHeadHopper.DeathHead.Handlers.JumpHandler), "JumpHead")]
    internal static class JumpHandlerJumpHeadBufferPatch
    {
        static void Postfix(DeathHeadHopper.DeathHead.Handlers.JumpHandler __instance)
        {
            DHHInputBridge.ExtendJumpBufferIfNeeded(__instance);
        }
    }
}
