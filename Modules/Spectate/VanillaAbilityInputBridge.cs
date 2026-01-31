#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeathHeadHopper.Managers;
using DeathHeadHopper.UI;
using DeathHeadHopperVRBridge.Modules.Config;
using HarmonyLib;
using UnityEngine;
using BepInEx.Logging;
using DeathHeadHopperVRBridge.Modules.Logging;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    /// <summary>Bridges stick-clicks to the vanilla DeathHeadHopper ability UI when spectating.</summary>
    internal sealed class VanillaAbilityInputBridge : MonoBehaviour
    {
        private const int SlotCount = 3;

        private static readonly Type? RepoXRActionsType = AccessTools.TypeByName("RepoXR.Input.Actions");
        private static readonly PropertyInfo? RepoXRActionsInstance =
            RepoXRActionsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo? RepoXRActionsIndexer =
            RepoXRActionsType?.GetProperty("Item", new[] { typeof(string) });

        private static readonly Type? InputActionType = AccessTools.TypeByName("UnityEngine.InputSystem.InputAction");
        private static readonly MethodInfo? WasPressedMethod =
            InputActionType?.GetMethod("WasPressedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? WasReleasedMethod =
            InputActionType?.GetMethod("WasReleasedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? IsPressedMethod =
            InputActionType?.GetMethod("IsPressed", Type.EmptyTypes);

        private static readonly PropertyInfo? DhhInstanceProperty = AccessTools.Property(typeof(DHHAbilityManager), "instance");
        private static readonly FieldInfo? AbilitySpotsField = AccessTools.Field(typeof(DHHAbilityManager), "abilitySpots");
        private static readonly MethodInfo? HandleInputDownMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputDown", new[] { typeof(AbilitySpot) });
        private static readonly MethodInfo? HandleInputHoldMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputHold", new[] { typeof(AbilitySpot) });
        private static readonly MethodInfo? HandleInputUpMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HandleInputUp", new[] { typeof(AbilitySpot) });
        private static readonly MethodInfo? HasEquippedAbilityMethod =
            AccessTools.Method(typeof(DHHAbilityManager), "HasEquippedAbility");
        private static readonly FieldInfo? BackgroundIconField = AccessTools.Field(typeof(AbilitySpot), "backgroundIcon");

        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("DeathHeadHopperFix-VR.VanillaAbility");
        internal const string ModuleTag = "[DeathHeadHopperFix-VR] [VanillaAbility]";

        private static VanillaAbilityInputBridge? _instance;

        private AbilitySpot? _slotDownSpot;
        private object? _activeAction;
        private Quaternion? _savedAvatarRotation;
        private const int PrimarySlotIndex = 0;

        public static void EnsureAttached(GameObject host)
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("DeathHeadHopperVRBridge_VanillaAbilityBridge");
            DontDestroyOnLoad(go);
            go.transform.SetParent(host.transform, false);
            _instance = go.AddComponent<VanillaAbilityInputBridge>();
        }

        private void Update()
        {
            if (!FeatureFlags.EnableVanillaAbilityBridge)
            {
                ReleaseAbility();
                return;
            }

            if (!SpectateHeadBridge.IsSpectatingHead())
            {
                ReleaseAbility();
                return;
            }

            if (!TryPreparePrimarySpot(out var spots, out var primarySpot))
            {
                ReleaseAbility();
                return;
            }

            var gripActive = SpectateHeadBridge.IsGripPressedForAbility();
            LogDebug("ListeningInputs", gripActive
                ? "Listening for the primary ability slot while DeathHeadHopper is active and the configured ability grip is held."
                : "Waiting for the configured ability grip before reacting to the primary ability slot.");
            HandleActivation(primarySpot!, gripActive);
        }

        private bool TryPreparePrimarySpot(out AbilitySpot[]? spots, out AbilitySpot? primarySpot)
        {
            spots = null;
            primarySpot = null;
            var manager = GetAbilityManager();
            if (manager == null)
            {
                return false;
            }

            if (!ManagerHasEquipped(manager))
            {
                return false;
            }

            spots = GetAbilitySpots(manager);
            if (spots == null || spots.Length == 0)
            {
                return false;
            }

            var spot = GetAbilitySpot(spots, PrimarySlotIndex);
            if (spot == null || spot.CurrentAbility == null)
            {
                LogDebug("PrimarySlotMissing", "Primary ability slot is empty while DeathHeadHopper is active.");
                return false;
            }

            primarySpot = spot;
            return true;
        }

        private void HandleActivation(AbilitySpot spot, bool allowStart)
        {
            var actionNames = ParseActionNames(FeatureFlags.AbilityActivateAction).ToArray();
            var action = GetFirstAvailableAction(actionNames);
            if (action == null)
            {
                ReleaseAbility();
                LogDebug("ActivateActionMissing", $"Activate action not found ({string.Join(", ", actionNames)})");
                return;
            }

            LogDebug("ActivateActionReady", $"Activate action ready ({string.Join(", ", actionNames)}). Primary slot index={PrimarySlotIndex}");

            if (allowStart && WasActionPressed(action))
            {
                LogDebug("ActivateActionTriggered", "Left stick click (L3) triggered activation for the primary slot while DeathHeadHopper is active.");
                ReleaseAbility();

                AlignAvatarToCamera();
                InvokeAbilityMethod(HandleInputDownMethod, spot);
                _slotDownSpot = spot;
                _activeAction = action;
                LogDebug("AbilityStart", $"Started primary ability slot {PrimarySlotIndex} ({spot.CurrentAbility?.GetType().Name ?? "none"})");
            }

            if (_slotDownSpot != null && _activeAction != null)
            {
                if (IsActionPressed(_activeAction))
                {
                    InvokeAbilityMethod(HandleInputHoldMethod, _slotDownSpot);
                    LogDebug("AbilityHold", $"Holding primary ability slot {PrimarySlotIndex} ({_slotDownSpot.CurrentAbility?.GetType().Name ?? "none"})", 0.5f);
                }

                if (WasActionReleased(_activeAction))
                {
                    ReleaseAbility();
                }
            }
        }

        private void ReleaseAbility()
        {
            if (_slotDownSpot == null)
            {
                _activeAction = null;
                return;
            }

            InvokeAbilityMethod(HandleInputUpMethod, _slotDownSpot);
            LogDebug("AbilityRelease", $"Released primary ability slot {PrimarySlotIndex}");
            _slotDownSpot = null;
            _activeAction = null;
            RestoreAvatarAlignment();
        }

        private static AbilitySpot? GetAbilitySpot(AbilitySpot[] spots, int index)
        {
            if (spots.Length == 0)
            {
                return null;
            }

            var clamped = index;
            if (clamped < 0)
            {
                clamped = 0;
            }

            if (clamped >= spots.Length)
            {
                clamped = spots.Length - 1;
            }

            return spots[clamped];
        }

        private void AlignAvatarToCamera()
        {
            if (_savedAvatarRotation.HasValue)
            {
                return;
            }

            var avatar = PlayerAvatar.instance;
            if (avatar == null)
            {
                return;
            }

            _savedAvatarRotation = avatar.transform.rotation;
            var forward = DHHInputBridge.GetAbilityForward();
            forward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
            {
                return;
            }

            avatar.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            LogDebug("AbilityOrient", $"Aligned avatar forward to {forward:F3}");
        }

        private void RestoreAvatarAlignment()
        {
            var avatar = PlayerAvatar.instance;
            if (avatar == null || !_savedAvatarRotation.HasValue)
            {
                return;
            }

            avatar.transform.rotation = _savedAvatarRotation.Value;
            _savedAvatarRotation = null;
            LogDebug("AbilityOrient", "Restored avatar rotation");
        }

        private static IEnumerable<string> ParseActionNames(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            foreach (var part in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    yield return trimmed;
                }
            }
        }

        private static object? GetFirstAvailableAction(IEnumerable<string> names)
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

        private static bool WasActionPressed(object? action)
        {
            return InvokeBooleanMethod(WasPressedMethod, action);
        }

        private static bool WasActionReleased(object? action)
        {
            return InvokeBooleanMethod(WasReleasedMethod, action);
        }

        private static bool IsActionPressed(object? action)
        {
            return InvokeBooleanMethod(IsPressedMethod, action);
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

        private static DHHAbilityManager? GetAbilityManager()
        {
            return DhhInstanceProperty?.GetValue(null) as DHHAbilityManager;
        }

        private static AbilitySpot[]? GetAbilitySpots(DHHAbilityManager? manager)
        {
            if (AbilitySpotsField == null || manager == null)
            {
                return null;
            }

            return AbilitySpotsField.GetValue(manager) as AbilitySpot[];
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

        private void LogDebug(string key, string message, float interval = 0.5f)
        {
            if (!FeatureFlags.DebugAbility)
            {
                return;
            }

            if (!LogLimiter.Allow(key, interval))
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        private void OnDestroy()
        {
            _instance = null;
        }
    }
}
