#nullable enable

using System;
using System.Linq;
using System.Reflection;
using DeathHeadHopper.Managers;
using DeathHeadHopper.UI;
using DeathHeadHopperVRBridge.Modules.Config;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    /// <summary>Shows a VR cursor that routes the three DeathHeadHopper ability slots when spectating the head.</summary>
    internal sealed class VrAbilityBarBridge : MonoBehaviour
    {
        private const int SlotCount = 3;
        private static readonly string[] ConfirmActionNames = { "Grab", "Interact", "Push" };

        private static readonly Type? RepoXRActionsType = AccessTools.TypeByName("RepoXR.Input.Actions");
        private static readonly PropertyInfo? RepoXRActionsInstance =
            RepoXRActionsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo? RepoXRActionsIndexer =
            RepoXRActionsType?.GetProperty("Item", new[] { typeof(string) });

        private static readonly Type? InputActionType = AccessTools.TypeByName("UnityEngine.InputSystem.InputAction");
        private static readonly MethodInfo? ReadValueGenericDefinition = InputActionType?.GetMethods().FirstOrDefault(
            m => m.Name == "ReadValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        private static readonly MethodInfo? WasPressedThisFrameMethod = InputActionType?.GetMethod("WasPressedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? WasReleasedThisFrameMethod = InputActionType?.GetMethod("WasReleasedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? IsPressedMethod = InputActionType?.GetMethod("IsPressed", Type.EmptyTypes);

        private static readonly PropertyInfo? DhhInstanceProperty = AccessTools.Property(typeof(DHHAbilityManager), "instance");
        private static readonly FieldInfo? SpectateCameraStateField = AccessTools.Field(typeof(SpectateCamera), "currentState");
        private static readonly MethodInfo? HasEquippedAbilityMethod = AccessTools.Method(typeof(DHHAbilityManager), "HasEquippedAbility");

        private static readonly FieldInfo? AbilitySpotsField = AccessTools.Field(typeof(DHHAbilityManager), "abilitySpots");
        private static readonly MethodInfo? HandleInputDownMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputDown", new[] { typeof(AbilitySpot) });
        private static readonly MethodInfo? HandleInputHoldMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputHold", new[] { typeof(AbilitySpot) });
        private static readonly MethodInfo? HandleInputUpMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputUp", new[] { typeof(AbilitySpot) });

        private static VrAbilityBarBridge? _instance;
        private readonly GameObject[] _markers = new GameObject[SlotCount];
        private readonly Renderer[] _renderers = new Renderer[SlotCount];
        private readonly Material[] _materials = new Material[SlotCount];
        private int _selectedSlot = 1;
        private int _slotDown = -1;
        private bool _visible;

        public static void EnsureAttached(GameObject host)
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("DeathHeadHopperVRBridge_VrAbilityBridge");
            DontDestroyOnLoad(go);
            go.transform.SetParent(host.transform, false);
            _instance = go.AddComponent<VrAbilityBarBridge>();
        }

        private void Awake()
        {
            for (var i = 0; i < SlotCount; i++)
            {
                _markers[i] = CreateMarker($"AbilitySlot{i}");
                _renderers[i] = _markers[i].GetComponent<Renderer>()!;
                _materials[i] = _renderers[i].material;
                _renderers[i].enabled = false;
            }
        }

        private void Update()
        {
            var camera = SpectateCamera.instance;
            var abilityManager = GetAbilityManager();
            if (camera == null || abilityManager == null)
            {
                SetMarkersVisible(false);
                ReleaseAbility();
                return;
            }

            var spectatingHead = GetSpectateState() == SpectateCamera.State.Head;
            if (!spectatingHead || !ManagerHasEquipped(abilityManager))
            {
                SetMarkersVisible(false);
                ReleaseAbility();
                return;
            }

            var gripPressed = SpectateHeadBridge.IsGripPressedForAbility();
            SetMarkersVisible(gripPressed);
            if (!gripPressed)
            {
                ReleaseAbility();
                return;
            }

            UpdateSelection(ReadTurnValue());
            if (_visible)
            {
                UpdateMarkerVisuals();
                PositionMarkers(camera.transform);
            }

            var confirmAction = GetFirstAvailableAction(ConfirmActionNames);
            TryStartAbility(confirmAction);

            if (_slotDown == -1)
            {
                return;
            }

            if (confirmAction != null && IsActionPressed(confirmAction))
            {
                MaintainAbilityHold();
            }

            if (confirmAction == null || WasActionReleased(confirmAction))
            {
                ReleaseAbility();
            }
        }

        private GameObject CreateMarker(string name)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = name;
            marker.hideFlags = HideFlags.HideAndDontSave;
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var renderer = marker.GetComponent<Renderer>()!;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.material = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Standard"))
            {
                color = new Color(0.08f, 0.45f, 0.9f, 0.35f),
                hideFlags = HideFlags.HideAndDontSave
            };
            return marker;
        }

        private void PositionMarkers(Transform cameraTransform)
        {
            if (!_visible)
            {
                return;
            }

            var basePosition = cameraTransform.position
                               + cameraTransform.forward * FeatureFlags.AbilityCursorDistance
                               + Vector3.up * FeatureFlags.AbilityCursorVerticalOffset;
            var baseRotation = Quaternion.LookRotation(cameraTransform.forward, Vector3.up);
            for (var i = 0; i < SlotCount; i++)
            {
                var offset = baseRotation * Vector3.right * ((i - 1) * FeatureFlags.AbilityCursorSpacing);
                var marker = _markers[i];
                marker.transform.position = basePosition + offset;
                marker.transform.rotation = baseRotation;
                var scale = FeatureFlags.AbilityCursorScale * (i == _selectedSlot ? 1.45f : 1f);
                marker.transform.localScale = Vector3.one * scale;
            }
        }

        private void SetMarkersVisible(bool visible)
        {
            if (_visible == visible)
            {
                return;
            }

            _visible = visible;
            for (var i = 0; i < SlotCount; i++)
            {
                _renderers[i].enabled = visible;
            }
        }

        private void UpdateSelection(float turnValue)
        {
            if (turnValue < -FeatureFlags.AbilitySelectionDeadzone)
            {
                _selectedSlot = 0;
            }
            else if (turnValue > FeatureFlags.AbilitySelectionDeadzone)
            {
                _selectedSlot = 2;
            }
            else
            {
                _selectedSlot = 1;
            }
        }

        private void UpdateMarkerVisuals()
        {
            if (!_visible)
            {
                return;
            }

            var baseColor = new Color(0.08f, 0.45f, 0.9f, 0.35f);
            var highlightColor = new Color(1f, 0.85f, 0.25f, 0.95f);
            for (var i = 0; i < SlotCount; i++)
            {
                _materials[i].color = i == _selectedSlot ? highlightColor : baseColor;
            }
        }

        private void TryStartAbility(object? confirmAction)
        {
            if (confirmAction == null || !WasActionPressed(confirmAction))
            {
                return;
            }

            ReleaseAbility();

            var spot = GetAbilitySpot(_selectedSlot);
            if (spot == null || spot.CurrentAbility == null)
            {
                return;
            }

            InvokeAbilityMethod(HandleInputDownMethod, spot);
            _slotDown = _selectedSlot;
        }

        private void MaintainAbilityHold()
        {
            if (_slotDown == -1)
            {
                return;
            }

            var spot = GetAbilitySpot(_slotDown);
            if (spot == null)
            {
                _slotDown = -1;
                return;
            }

            InvokeAbilityMethod(HandleInputHoldMethod, spot);
        }

        private void ReleaseAbility()
        {
            if (_slotDown == -1)
            {
                return;
            }

            var spot = GetAbilitySpot(_slotDown);
            if (spot != null)
            {
                InvokeAbilityMethod(HandleInputUpMethod, spot);
            }

            _slotDown = -1;
        }

        private AbilitySpot? GetAbilitySpot(int index)
        {
            var manager = GetAbilityManager();
            if (AbilitySpotsField == null || manager == null)
            {
                return null;
            }

            var spots = AbilitySpotsField.GetValue(manager) as AbilitySpot[];
            if (spots == null || index < 0 || index >= spots.Length)
            {
                return null;
            }

            return spots[index];
        }

        private object? GetFirstAvailableAction(string[] names)
        {
            foreach (var name in names)
            {
                var action = GetAction(name);
                if (action != null)
                {
                    return action;
                }
            }

            return null;
        }

        private static object? GetAction(string name)
        {
            var instance = RepoXRActionsInstance?.GetValue(null);
            if (instance == null || RepoXRActionsIndexer == null)
            {
                return null;
            }

            try
            {
                return RepoXRActionsIndexer.GetValue(instance, new object[] { name });
            }
            catch
            {
                return null;
            }
        }

        private static bool IsActionPressed(object? action)
        {
            return InvokeBooleanMethod(IsPressedMethod, action);
        }

        private static bool WasActionPressed(object? action)
        {
            return InvokeBooleanMethod(WasPressedThisFrameMethod, action);
        }

        private static bool WasActionReleased(object? action)
        {
            return InvokeBooleanMethod(WasReleasedThisFrameMethod, action);
        }

        private float ReadTurnValue()
        {
            return ReadFloat(GetAction("Turn"));
        }

        private static float ReadFloat(object? action)
        {
            if (action == null || ReadValueGenericDefinition == null)
            {
                return 0f;
            }

            try
            {
                var generic = ReadValueGenericDefinition.MakeGenericMethod(typeof(float));
                var result = generic.Invoke(action, Array.Empty<object>());
                return result is float value ? value : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static bool InvokeBooleanMethod(MethodInfo? method, object? action)
        {
            if (method == null || action == null)
            {
                return false;
            }

            try
            {
                var result = method.Invoke(action, Array.Empty<object>());
                return result is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static void InvokeAbilityMethod(MethodInfo? method, AbilitySpot spot)
        {
            var manager = GetAbilityManager();
            if (method == null || manager == null)
            {
                return;
            }

            try
            {
                method.Invoke(manager, new object[] { spot });
            }
            catch
            {
                // ignore reflection failures
            }
        }

        private static DHHAbilityManager? GetAbilityManager()
        {
            return DhhInstanceProperty?.GetValue(null) as DHHAbilityManager;
        }

        private static SpectateCamera.State? GetSpectateState()
        {
            var spectate = SpectateCamera.instance;
            if (spectate == null || SpectateCameraStateField == null)
            {
                return null;
            }

            var value = SpectateCameraStateField.GetValue(spectate);
            return value is SpectateCamera.State state ? state : null;
        }

        private static bool ManagerHasEquipped(DHHAbilityManager manager)
        {
            if (HasEquippedAbilityMethod == null)
            {
                return false;
            }

            try
            {
                var result = HasEquippedAbilityMethod.Invoke(manager, Array.Empty<object>());
                return result is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private void OnDestroy()
        {
            foreach (var marker in _markers)
            {
                if (marker != null)
                {
                    Destroy(marker);
                }
            }

            foreach (var material in _materials)
            {
                if (material != null)
                {
                    Destroy(material);
                }
            }

            _instance = null;
        }
    }
}
