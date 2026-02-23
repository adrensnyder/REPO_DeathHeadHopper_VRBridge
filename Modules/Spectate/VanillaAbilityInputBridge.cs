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
using UnityEngine.InputSystem;
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
        private static readonly Type? RepoXRInputSystemType = AccessTools.TypeByName("RepoXR.Input.VRInputSystem");
        private static readonly PropertyInfo? RepoXRInputSystemInstance =
            RepoXRInputSystemType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo? RepoXRInputSystemActions =
            RepoXRInputSystemType?.GetProperty("Actions", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo? RepoXRInputSystemCurrentControlScheme =
            RepoXRInputSystemType?.GetProperty("CurrentControlScheme", BindingFlags.Public | BindingFlags.Instance);

        private static readonly Type? InputActionRuntimeType = AccessTools.TypeByName("UnityEngine.InputSystem.InputAction");
        private static readonly MethodInfo? WasPressedMethod =
            InputActionRuntimeType?.GetMethod("WasPressedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? WasReleasedMethod =
            InputActionRuntimeType?.GetMethod("WasReleasedThisFrame", Type.EmptyTypes);
        private static readonly MethodInfo? IsPressedMethod =
            InputActionRuntimeType?.GetMethod("IsPressed", Type.EmptyTypes);

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
        private static readonly string[] FallbackActivateActionNames =
        {
            "VR Actions/ResetHeight",
            "VR Actions/Grab",
            "VR Actions/Interact",
            "VR Actions/Push",
            "VR Actions/Movement",
            "VR Actions/Turn",
            "ResetHeight"
        };
        private static readonly HashSet<string> ExcludedActionNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Movement",
            "Turn",
            "Scroll",
            "Map",
            "GripLeft",
            "leftGrip",
            "GripRight",
            "rightGrip",
            "MapGrabLeft",
            "MapGrabRight",
            "VR Actions/Movement",
            "VR Actions/Turn",
            "VR Actions/Map",
            "VR Actions/GripLeft",
            "VR Actions/leftGrip",
            "VR Actions/GripRight",
            "VR Actions/rightGrip",
            "VR Actions/MapGrabLeft",
            "VR Actions/MapGrabRight",
        };

        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("DeathHeadHopperFix-VR.VanillaAbility");
        internal const string ModuleTag = "[DeathHeadHopperFix-VR] [VanillaAbility]";

        private static VanillaAbilityInputBridge? _instance;
        private static readonly Dictionary<InputKey, CustomInputActionEntry> CustomInputActions = new();
        private static float s_debugBurstUntilTime = -1f;

        private AbilitySpot? _slotDownSpot;
        private object? _activeAction;
        private InputKey? _activeInputKey;
        private Quaternion? _savedAvatarRotation;
        private int _activeSlotIndex = -1;
        private const int PrimarySlotIndex = 0;
        private static int s_suppressDirectionBindingDownFrame = -1;
        private static int s_suppressDirectionBindingUpFrame = -1;

        private readonly struct RuntimeInputBindingResolution
        {
            internal RuntimeInputBindingResolution(InputKey key, InputAction action, int bindingIndex, string controlScheme, string effectivePath)
            {
                Key = key;
                Action = action;
                BindingIndex = bindingIndex;
                ControlScheme = controlScheme;
                EffectivePath = effectivePath;
            }

            internal InputKey Key { get; }
            internal InputAction Action { get; }
            internal int BindingIndex { get; }
            internal string ControlScheme { get; }
            internal string EffectivePath { get; }
        }

        private sealed class CustomInputActionEntry
        {
            internal InputAction Action = null!;
            internal string EffectivePath = string.Empty;
            internal string ControlScheme = string.Empty;
        }

        public static void EnsureAttached(GameObject host)
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("DeathHeadHopperVRBridge_VanillaAbilityBridge");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VanillaAbilityInputBridge>();
        }

        private static void EnsureAttachedRuntime()
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("DeathHeadHopperVRBridge_VanillaAbilityBridge");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VanillaAbilityInputBridge>();
        }

        private void Update()
        {
            if (!FeatureFlags.EnableVanillaAbilityBridge)
            {
                LogCoreFlow("Core.Disabled", "Update skipped: EnableVanillaAbilityBridge=false");
                ReleaseAbility();
                return;
            }

            if (!IsAbilityRuntimeContextActive())
            {
                LogInputFlow("CtxOff",
                    $"Ability context inactive. runtime={SpectateHeadBridge.IsDhhRuntimeInputContextActive()} localTriggered={SpectateHeadBridge.IsLocalDeathHeadTriggered()} spectatingHead={SpectateHeadBridge.IsSpectatingHead()}");
                ReleaseAbility();
                return;
            }

            if (!TryPrepareSpots(out var spots))
            {
                LogCoreFlow("Core.PrepareFail", "Update skipped: TryPrepareSpots returned false");
                ReleaseAbility();
                return;
            }

            var gripActive = SpectateHeadBridge.IsGripPressedForAbility();
            LogDebug("ListeningInputs", gripActive
                ? "Listening for slot 1/slot 2 ability actions while DeathHeadHopper is active and the configured ability grip is held."
                : "Waiting for the configured ability grip before reacting to slot 1/slot 2 ability actions.");
            HandleActivation(spots!, gripActive);
        }

        private bool TryPrepareSpots(out AbilitySpot[]? spots)
        {
            spots = null;
            var manager = GetAbilityManager();
            if (manager == null)
            {
                LogCoreFlow("Prepare.NoManager", "TryPrepareSpots: DHHAbilityManager.instance is null");
                return false;
            }

            if (!ManagerHasEquipped(manager))
            {
                LogCoreFlow("Prepare.NoEquipped", "TryPrepareSpots: HasEquippedAbility=false");
                return false;
            }

            spots = GetAbilitySpots(manager);
            if (spots == null || spots.Length == 0)
            {
                LogCoreFlow("Prepare.NoSpots", $"TryPrepareSpots: spots invalid (null={spots == null}, len={(spots?.Length ?? 0)})");
                return false;
            }

            var primarySpot = GetAbilitySpot(spots, PrimarySlotIndex);
            var directionSlotIndex = GetDirectionSlotIndex();
            var directionSpot = GetAbilitySpot(spots, directionSlotIndex);
            LogInputFlow("SpotsState",
                $"spotsFeched={spots.Length > 0} slot1Ability={(primarySpot?.CurrentAbility?.GetType().Name ?? "none")} slot{directionSlotIndex + 1}Ability={(directionSpot?.CurrentAbility?.GetType().Name ?? "none")}",
                0.25f);
            if ((primarySpot == null || primarySpot.CurrentAbility == null)
                && (directionSpot == null || directionSpot.CurrentAbility == null))
            {
                LogDebug("AbilitySlotsMissing", "Slot 1 and slot 2 are empty while DeathHeadHopper is active.");
                return false;
            }

            return true;
        }

        private void HandleActivation(AbilitySpot[] spots, bool allowStart)
        {
            var directionSlotIndex = GetDirectionSlotIndex();
            var directionSlotLabel = $"slot {directionSlotIndex + 1}";
            var slot1Spot = GetAbilitySpot(spots, PrimarySlotIndex);
            var slot2Spot = GetAbilitySpot(spots, directionSlotIndex);
            var slot1InputKey = TryResolveConfiguredInputKey(FeatureFlags.AbilityActivateAction, out var parsedSlot1Key) ? parsedSlot1Key : (InputKey?)null;
            var slot2InputKey = TryResolveConfiguredInputKey(FeatureFlags.AbilityDirectionAction, out var parsedSlot2Key) ? parsedSlot2Key : (InputKey?)null;
            RuntimeInputBindingResolution slot1RuntimeBinding = default;
            var slot1RuntimeBindingResolved = slot1InputKey.HasValue && TryResolveRuntimeBindingForInputKey(slot1InputKey.Value, out slot1RuntimeBinding);
            RuntimeInputBindingResolution slot2RuntimeBinding = default;
            var slot2RuntimeBindingResolved = slot2InputKey.HasValue && TryResolveRuntimeBindingForInputKey(slot2InputKey.Value, out slot2RuntimeBinding);
            if (slot1RuntimeBindingResolved)
            {
                LogInputFlow("RuntimeBindingResolve",
                    $"slot1Key={slot1RuntimeBinding.Key} action={slot1RuntimeBinding.Action.name} scheme={(string.IsNullOrEmpty(slot1RuntimeBinding.ControlScheme) ? "none" : slot1RuntimeBinding.ControlScheme)} bindingIndex={slot1RuntimeBinding.BindingIndex} path={(string.IsNullOrEmpty(slot1RuntimeBinding.EffectivePath) ? "none" : slot1RuntimeBinding.EffectivePath)}",
                    0.5f);
            }
            if (slot2RuntimeBindingResolved)
            {
                LogInputFlow("RuntimeBindingResolve",
                    $"slot{directionSlotIndex + 1}Key={slot2RuntimeBinding.Key} action={slot2RuntimeBinding.Action.name} scheme={(string.IsNullOrEmpty(slot2RuntimeBinding.ControlScheme) ? "none" : slot2RuntimeBinding.ControlScheme)} bindingIndex={slot2RuntimeBinding.BindingIndex} path={(string.IsNullOrEmpty(slot2RuntimeBinding.EffectivePath) ? "none" : slot2RuntimeBinding.EffectivePath)}",
                    0.5f);
            }
            LogInputFlow("BindingSelection",
                $"slot1Config='{FeatureFlags.AbilityActivateAction}' slot1Key={(slot1InputKey?.ToString() ?? "none")} slot1HasAbility={(slot1Spot?.CurrentAbility != null)} " +
                $"slot{directionSlotIndex + 1}Config='{FeatureFlags.AbilityDirectionAction}' slot{directionSlotIndex + 1}Key={(slot2InputKey?.ToString() ?? "none")} slot{directionSlotIndex + 1}HasAbility={(slot2Spot?.CurrentAbility != null)}");

            // Resolve each input action only for slots that currently have an equipped ability.
            // This bridge reads input state and never consumes/cancels the underlying action.
            var slot1Action = (slot1Spot != null && slot1Spot.CurrentAbility != null)
                ? ResolveAction(FeatureFlags.AbilityActivateAction, "ActivateAction", "slot 1", PrimarySlotIndex)
                : null;
            var slot2Action = (slot2Spot != null && slot2Spot.CurrentAbility != null)
                ? ResolveAction(FeatureFlags.AbilityDirectionAction, "DirectionAction", directionSlotLabel, directionSlotIndex)
                : null;

            if (_slotDownSpot != null && _activeAction != null)
            {
                if (IsActionPressed(_activeAction))
                {
                    InvokeAbilityMethod(HandleInputHoldMethod, _slotDownSpot);
                    LogDebug("AbilityHold", $"Holding active ability slot ({_slotDownSpot.CurrentAbility?.GetType().Name ?? "none"})", 30);
                }

                if (WasActionReleased(_activeAction))
                {
                    ReleaseAbility();
                }
            }
            else if (_slotDownSpot != null && _activeInputKey.HasValue)
            {
                if (SemiFunc.InputHold(_activeInputKey.Value))
                {
                    InvokeAbilityMethod(HandleInputHoldMethod, _slotDownSpot);
                }
                else
                {
                    ReleaseAbility();
                }
            }

            if (!allowStart)
            {
                return;
            }

            var slot1Triggered = IsActionTriggeredThisFrame(slot1Action, FeatureFlags.AbilityActivateAction);
            var preferConfiguredVrAction = ContainsExplicitVrActionToken(FeatureFlags.AbilityDirectionAction);
            object? slot2TriggerAction;
            if (preferConfiguredVrAction)
            {
                slot2TriggerAction = slot2Action ?? (slot2RuntimeBindingResolved ? slot2RuntimeBinding.Action : null);
            }
            else
            {
                slot2TriggerAction = slot2RuntimeBindingResolved ? slot2RuntimeBinding.Action : slot2Action;
            }
            var slot2Triggered = IsActionTriggeredThisFrame(slot2TriggerAction, FeatureFlags.AbilityDirectionAction);
            LogInputFlow("TriggerState",
                $"Grip={allowStart} slot1={slot1Triggered} slot{directionSlotIndex + 1}={slot2Triggered} slot1Action={(slot1Action != null)} slot{directionSlotIndex + 1}Action={(slot2Action != null)} slot{directionSlotIndex + 1}RuntimeAction={slot2RuntimeBindingResolved} slot{directionSlotIndex + 1}PreferVrAction={preferConfiguredVrAction} slot1Key={(slot1InputKey?.ToString() ?? "none")} slot{directionSlotIndex + 1}Key={(slot2InputKey?.ToString() ?? "none")}");

            if (slot1Spot != null && slot1Spot.CurrentAbility != null && slot1Triggered && (slot1Action != null || slot1InputKey.HasValue))
            {
                LogDebug("ActivateActionTriggered", "Configured activate action triggered direct activation of slot 1 while DeathHeadHopper is active.");
                StartAbility(slot1Spot, slot1Action, PrimarySlotIndex, slot1InputKey);
            }
            else if (slot2Spot != null && slot2Spot.CurrentAbility != null && slot2Triggered && (slot2Action != null || slot2InputKey.HasValue))
            {
                LogDebug("DirectionActionTriggered", $"Configured direction action triggered activation for {directionSlotLabel} while DeathHeadHopper is active.");
                StartAbility(slot2Spot, slot2Action, directionSlotIndex, slot2InputKey);
            }
        }

        private object? ResolveAction(string rawConfig, string keyPrefix, string slotLabel, int slotIndex)
        {
            foreach (var configured in ParseActionNames(rawConfig))
            {
                if (TryResolveInputKeyAction(configured, out var mappedAction, out var mappedLabel))
                {
                    LogDebug($"{keyPrefix}Ready", $"Action ready for {slotLabel}: {mappedLabel}. Slot index={slotIndex}");
                    return mappedAction;
                }
            }

            var actionNames = BuildActivateActionCandidates(rawConfig);
            var action = GetFirstAvailableAction(actionNames, out var selectedActionName);
            if (action == null)
            {
                LogDebug($"{keyPrefix}Missing",
                    $"Action not found for {slotLabel}. Config='{rawConfig}'. Tried={string.Join(", ", actionNames.Take(12))}");
                return null;
            }

            LogDebug($"{keyPrefix}Ready", $"Action ready for {slotLabel}: {selectedActionName}. Slot index={slotIndex}");
            return action;
        }

        private void StartAbility(AbilitySpot spot, object? action, int slotIndex, InputKey? inputKey)
        {
            ReleaseAbility();
            AlignAvatarToCamera();
            InvokeAbilityMethod(HandleInputDownMethod, spot);
            _slotDownSpot = spot;
            _activeAction = action;
            _activeInputKey = inputKey;
            _activeSlotIndex = slotIndex;
            if (slotIndex == GetDirectionSlotIndex())
            {
                // Consume configured direction InputDown in the same frame when the direction slot is activated.
                s_suppressDirectionBindingDownFrame = Time.frameCount;
                s_suppressDirectionBindingUpFrame = Time.frameCount;
                LogInputFlow("DirectionSlotStart", $"slot={slotIndex + 1} start frame={Time.frameCount}; suppression armed.");
            }
            LogInputFlow("AbilityStart",
                $"slot={slotIndex} ability={(spot.CurrentAbility?.GetType().Name ?? "none")} actionObj={(action != null)} inputKey={(inputKey?.ToString() ?? "none")} frame={Time.frameCount}",
                0.15f);
            LogDebug("AbilityStart", $"Started ability slot {slotIndex} ({spot.CurrentAbility?.GetType().Name ?? "none"})");
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
            _activeInputKey = null;
            _activeSlotIndex = -1;
            RestoreAvatarAlignment();
        }

        internal static bool ShouldSuppressDirectionBindingInputDownThisFrame()
        {
            if (_instance == null || !FeatureFlags.EnableVanillaAbilityBridge || !IsAbilityRuntimeContextActive())
            {
                return false;
            }

            return s_suppressDirectionBindingDownFrame == Time.frameCount;
        }

        internal static bool ShouldSuppressDirectionBindingInputDownThisFrame(InputKey key)
        {
            if (!HasDirectionAbilityAvailable())
            {
                return false;
            }

            var hasConfigured = TryGetDirectionInputKey(out var configuredKey);
            var match = hasConfigured && configuredKey == key;
            if (!match)
            {
                return false;
            }

            var armedFrame = ShouldSuppressDirectionBindingInputDownThisFrame();
            var gripSuppression = IsAbilityRuntimeContextActive() && SpectateHeadBridge.IsGripPressedForAbility();
            var shouldSuppress = armedFrame || gripSuppression;
            if (ShouldLogAbilityFlow("AbilityFlow.SuppressCheck", 0.2f))
            {
                Log.LogInfo($"{ModuleTag} Suppress check: key={key} configured={configuredKey} match={match} armed={armedFrame} gripSuppression={gripSuppression} frame={Time.frameCount}");
            }

            return shouldSuppress;
        }

        internal static bool ShouldSuppressDirectionBindingInputHoldThisFrame(InputKey key)
        {
            if (!HasDirectionAbilityAvailable())
            {
                return false;
            }

            if (!TryGetDirectionInputKey(out var configuredKey) || configuredKey != key)
            {
                return false;
            }

            return IsAbilityRuntimeContextActive() && SpectateHeadBridge.IsGripPressedForAbility();
        }

        internal static bool ShouldSuppressDirectionBindingInputUpThisFrame(InputKey key)
        {
            if (!HasDirectionAbilityAvailable())
            {
                return false;
            }

            if (!TryGetDirectionInputKey(out var configuredKey) || configuredKey != key)
            {
                return false;
            }

            return s_suppressDirectionBindingUpFrame == Time.frameCount
                || (IsAbilityRuntimeContextActive() && SpectateHeadBridge.IsGripPressedForAbility());
        }

        internal static bool IsDirectionAbilityActive()
        {
            if (_instance == null || !FeatureFlags.EnableVanillaAbilityBridge || !IsAbilityRuntimeContextActive())
            {
                return false;
            }

            return _instance._activeSlotIndex == GetDirectionSlotIndex()
                && _instance._slotDownSpot != null
                && (_instance._activeAction != null || _instance._activeInputKey.HasValue);
        }

        internal static bool IsDirectionBindingHeld()
        {
            if (!HasDirectionAbilityAvailable())
            {
                return false;
            }

            if (!TryGetDirectionInputKey(out var key))
            {
                return false;
            }

            return SemiFunc.InputHold(key);
        }


        internal static bool HasDirectionAbilityAvailable()
        {
            if (_instance == null || !FeatureFlags.EnableVanillaAbilityBridge || !IsAbilityRuntimeContextActive())
            {
                return false;
            }

            var manager = GetAbilityManager();
            if (manager == null || !ManagerHasEquipped(manager))
            {
                return false;
            }

            var spots = GetAbilitySpots(manager);
            if (spots == null || spots.Length == 0)
            {
                return false;
            }

            var directionSpot = GetAbilitySpot(spots, GetDirectionSlotIndex());
            var available = directionSpot != null && directionSpot.CurrentAbility != null;
            if (ShouldLogAbilityFlow("AbilityFlow.DirectionAvailability", 0.5f))
            {
                Log.LogInfo($"{ModuleTag} Direction slot availability: slot={GetDirectionSlotIndex() + 1} available={available} ability={(directionSpot?.CurrentAbility?.GetType().Name ?? "none")}");
            }

            return available;
        }

        internal static bool TryGetConfiguredDirectionInputKey(out InputKey key)
        {
            return TryGetDirectionInputKey(out key);
        }

        internal static string GetDirectionSuppressionDebugState(InputKey incomingKey)
        {
            var abilityEnabled = FeatureFlags.EnableVanillaAbilityBridge;
            var hasInstance = _instance != null;
            var contextActive = IsAbilityRuntimeContextActive();
            var frameArmed = ShouldSuppressDirectionBindingInputDownThisFrame();
            var hasConfigured = TryGetDirectionInputKey(out var configuredKey);
            var keyMatch = hasConfigured && configuredKey == incomingKey;
            var gripPressed = SpectateHeadBridge.IsGripPressedForAbility();

            return $"enabled={abilityEnabled} instance={hasInstance} context={contextActive} armed={frameArmed} grip={gripPressed} incoming={incomingKey} configured={(hasConfigured ? configuredKey.ToString() : "none")} match={keyMatch}";
        }

        internal static void NotifyDirectionBindingAttempt(InputKey key, bool gripPressed)
        {
            if (!InternalDebugConfig.DebugAbilityInputFlow || !SpectateHeadBridge.IsLocalDeathHeadTriggered())
            {
                return;
            }

            if (!gripPressed)
            {
                return;
            }

            if (!TryGetDirectionInputKey(out var configuredKey) || configuredKey != key)
            {
                return;
            }

            s_debugBurstUntilTime = Time.realtimeSinceStartup + 2f;
            if (LogLimiter.Allow("AbilityFlow.BurstStart", 0.2f))
            {
                Log.LogInfo($"{ModuleTag} Debug burst armed for 2.0s on grip+{key}.");
            }
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

        private static IReadOnlyList<string> BuildActivateActionCandidates(string rawConfig)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return;
                }

                var trimmed = candidate.Trim();
                if (trimmed.Length == 0 || IsExcludedAction(trimmed) || !seen.Add(trimmed))
                {
                    return;
                }

                names.Add(trimmed);
            }

            foreach (var configured in ParseActionNames(rawConfig))
            {
                Add(configured);
            }

            foreach (var discovered in DiscoverRepoXRActionNames())
            {
                Add(discovered);
            }

            foreach (var fallback in FallbackActivateActionNames)
            {
                Add(fallback);
            }

            return names;
        }

        private static bool TryResolveInputKeyAction(string rawToken, out object? action, out string label)
        {
            action = null;
            label = string.Empty;
            var useCustomAction = IsCustomInputKeyAliasToken(rawToken);
            var token = useCustomAction ? NormalizeCustomInputKeyToken(rawToken) : NormalizeInputKeyToken(rawToken);
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (!Enum.TryParse(token, true, out InputKey parsedKey))
            {
                return false;
            }

            if (parsedKey == InputKey.Map)
            {
                return false;
            }

            if (useCustomAction)
            {
                if (!TryResolveRuntimeCustomActionForInputKey(parsedKey, out var customAction, out var customLabel))
                {
                    return false;
                }

                action = customAction;
                label = customLabel;
                return true;
            }

            if (!TryResolveRuntimeBindingForInputKey(parsedKey, out var runtimeBinding))
            {
                return false;
            }

            action = runtimeBinding.Action;
            label = $"InputKey.{parsedKey} [scheme={(string.IsNullOrEmpty(runtimeBinding.ControlScheme) ? "none" : runtimeBinding.ControlScheme)} idx={runtimeBinding.BindingIndex} path={(string.IsNullOrEmpty(runtimeBinding.EffectivePath) ? "none" : runtimeBinding.EffectivePath)}]";
            return true;
        }

        private static bool TryResolveRuntimeBindingForInputKey(InputKey key, out RuntimeInputBindingResolution resolution)
        {
            resolution = default;

            var manager = InputManager.instance;
            if (manager == null)
            {
                return false;
            }

            var mappedAction = manager.GetAction(key);
            if (mappedAction == null)
            {
                return false;
            }

            var currentScheme = string.Empty;
            try
            {
                var inputSystem = RepoXRInputSystemInstance?.GetValue(null);
                currentScheme = RepoXRInputSystemCurrentControlScheme?.GetValue(inputSystem) as string ?? string.Empty;
            }
            catch
            {
                currentScheme = string.Empty;
            }

            var bindingIndex = 0;
            try
            {
                if (!string.IsNullOrEmpty(currentScheme))
                {
                    bindingIndex = Mathf.Max(InputActionRebindingExtensions.GetBindingIndex(mappedAction, currentScheme, null), 0);
                }
            }
            catch
            {
                bindingIndex = 0;
            }

            var effectivePath = string.Empty;
            var bindingsCount = mappedAction.bindings.Count;
            if (bindingsCount > 0)
            {
                if (bindingIndex >= bindingsCount)
                {
                    bindingIndex = 0;
                }

                effectivePath = mappedAction.bindings[bindingIndex].effectivePath ?? string.Empty;
            }

            resolution = new RuntimeInputBindingResolution(key, mappedAction, bindingIndex, currentScheme, effectivePath);
            return true;
        }

        private static bool TryResolveRuntimeCustomActionForInputKey(InputKey key, out InputAction action, out string label)
        {
            action = null!;
            label = string.Empty;

            if (!TryResolveRuntimeBindingForInputKey(key, out var runtimeBinding))
            {
                return false;
            }

            var effectivePath = runtimeBinding.EffectivePath;
            if (string.IsNullOrEmpty(effectivePath))
            {
                return false;
            }

            if (!CustomInputActions.TryGetValue(key, out var entry)
                || entry.Action == null
                || !string.Equals(entry.EffectivePath, effectivePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.ControlScheme, runtimeBinding.ControlScheme, StringComparison.OrdinalIgnoreCase))
            {
                DisposeCustomInputAction(key);

                var customAction = new InputAction($"Custom{key}", UnityEngine.InputSystem.InputActionType.Button, effectivePath, null, null, null);
                customAction.Enable();
                entry = new CustomInputActionEntry
                {
                    Action = customAction,
                    EffectivePath = effectivePath,
                    ControlScheme = runtimeBinding.ControlScheme
                };
                CustomInputActions[key] = entry;
                LogInputFlow("CustomBindingMap",
                    $"Custom{key} mapped to InputKey.{key}: sourceAction={runtimeBinding.Action.name} scheme={(string.IsNullOrEmpty(runtimeBinding.ControlScheme) ? "none" : runtimeBinding.ControlScheme)} path={effectivePath}",
                    0.5f);
            }

            action = entry.Action;
            label = $"VR Actions/Custom{key} -> InputKey.{key} [scheme={(string.IsNullOrEmpty(runtimeBinding.ControlScheme) ? "none" : runtimeBinding.ControlScheme)} path={effectivePath}]";
            return true;
        }

        private static bool IsCustomInputKeyAliasToken(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return false;
            }

            var token = rawToken.Trim();
            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                token = token.Substring(1);
            }

            const string vrPrefix = "VR Actions/";
            if (token.StartsWith(vrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(vrPrefix.Length);
            }

            return token.StartsWith("Custom", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeInputKeyToken(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return string.Empty;
            }

            var token = rawToken.Trim();
            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                token = token.Substring(1);
            }

            const string prefix = "InputKey.";
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(prefix.Length);
            }

            return token.Trim();
        }

        private static string NormalizeCustomInputKeyToken(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return string.Empty;
            }

            var token = rawToken.Trim();
            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                token = token.Substring(1);
            }

            const string vrPrefix = "VR Actions/";
            if (token.StartsWith(vrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(vrPrefix.Length);
            }

            const string customPrefix = "Custom";
            if (token.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase) && token.Length > customPrefix.Length)
            {
                token = token.Substring(customPrefix.Length);
            }

            return NormalizeInputKeyToken(token);
        }

        private static bool TryGetDirectionInputKey(out InputKey key)
        {
            key = default;
            if (TryResolveConfiguredInputKey(FeatureFlags.AbilityDirectionAction, out var parsed))
            {
                key = parsed;
                return true;
            }

            if (InternalDebugConfig.DebugAbilityInputFlow
                && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                && LogLimiter.Allow("AbilityFlow.DirectionKeyMissing", 1f))
            {
                Log.LogInfo($"{ModuleTag} No InputKey parsed from AbilityDirectionAction='{FeatureFlags.AbilityDirectionAction}'.");
            }

            return false;
        }

        private static bool TryResolveConfiguredInputKey(string rawConfig, out InputKey key)
        {
            key = default;
            foreach (var configured in ParseActionNames(rawConfig))
            {
                var token = IsCustomInputKeyAliasToken(configured)
                    ? NormalizeCustomInputKeyToken(configured)
                    : NormalizeInputKeyToken(configured);
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!Enum.TryParse<InputKey>(token, true, out var parsedKey))
                {
                    continue;
                }

                if (parsedKey == InputKey.Map)
                {
                    continue;
                }

                key = parsedKey;
                return true;
            }

            return false;
        }

        private static bool IsExcludedAction(string candidate)
        {
            if (ExcludedActionNames.Contains(candidate))
            {
                return true;
            }

            const string vrPrefix = "VR Actions/";
            var normalized = candidate.Trim();
            if (normalized.StartsWith(vrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(vrPrefix.Length).Trim();
            }
            return ExcludedActionNames.Contains(normalized);
        }

        private static bool ContainsExplicitVrActionToken(string rawConfig)
        {
            const string vrPrefix = "VR Actions/";
            foreach (var configured in ParseActionNames(rawConfig))
            {
                if (configured.StartsWith(vrPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> DiscoverRepoXRActionNames()
        {
            object? inputSystem = null;
            InputActionAsset? inputActions = null;

            try
            {
                inputSystem = RepoXRInputSystemInstance?.GetValue(null);
                inputActions = RepoXRInputSystemActions?.GetValue(inputSystem) as InputActionAsset;
            }
            catch
            {
                yield break;
            }

            if (inputActions == null)
            {
                yield break;
            }

            foreach (var map in inputActions.actionMaps)
            {
                var mapName = map.name ?? string.Empty;
                foreach (var action in map.actions)
                {
                    var actionName = action.name;
                    if (string.IsNullOrWhiteSpace(actionName))
                    {
                        continue;
                    }

                    yield return actionName;
                    if (!string.IsNullOrWhiteSpace(mapName))
                    {
                        yield return mapName + "/" + actionName;
                    }
                }
            }
        }

        private static object? GetFirstAvailableAction(IEnumerable<string> names, out string selectedName)
        {
            selectedName = string.Empty;
            foreach (var name in names)
            {
                var action = GetAction(name);
                if (action != null)
                {
                    selectedName = name;
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

        private static bool IsActionTriggeredThisFrame(object? action, string rawConfig)
        {
            if (WasActionPressed(action))
            {
                if (InternalDebugConfig.DebugAbilityInputFlow
                    && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                    && LogLimiter.Allow("AbilityFlow.ActionPressed", 0.2f))
                {
                    Log.LogInfo($"{ModuleTag} Action pressed this frame via resolved action object. config='{rawConfig}' action={DescribeActionObject(action)}");
                }
                return true;
            }

            foreach (var configured in ParseActionNames(rawConfig))
            {
                var token = IsCustomInputKeyAliasToken(configured)
                    ? NormalizeCustomInputKeyToken(configured)
                    : NormalizeInputKeyToken(configured);
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!Enum.TryParse(token, true, out InputKey parsedKey))
                {
                    continue;
                }

                if (parsedKey == InputKey.Map)
                {
                    continue;
                }

                if (SemiFunc.InputDown(parsedKey))
                {
                    if (InternalDebugConfig.DebugAbilityInputFlow
                        && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                        && LogLimiter.Allow("AbilityFlow.InputKeyDown", 0.2f))
                    {
                        Log.LogInfo($"{ModuleTag} InputKey trigger detected: {parsedKey} from '{rawConfig}'.");
                    }
                    return true;
                }

                // Read the vanilla key state directly so slot2 trigger detection is not
                // masked by our own SemiFunc suppression patch.
                var inputManager = InputManager.instance;
                if (inputManager != null && inputManager.KeyDown(parsedKey))
                {
                    if (InternalDebugConfig.DebugAbilityInputFlow
                        && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                        && LogLimiter.Allow("AbilityFlow.InputKeyDownRaw", 0.2f))
                    {
                        Log.LogInfo($"{ModuleTag} Raw InputManager trigger detected: {parsedKey} from '{rawConfig}'.");
                    }
                    return true;
                }
            }

            return false;
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

        private static string DescribeActionObject(object? action)
        {
            if (action is InputAction inputAction)
            {
                var activePath = inputAction.activeControl?.path ?? "none";
                return $"name={inputAction.name} activePath={activePath}";
            }

            return action?.GetType().Name ?? "none";
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
                if (InternalDebugConfig.DebugAbilityInputFlow
                    && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                    && LogLimiter.Allow("AbilityFlow.InvokeMissing", 0.5f))
                {
                    Log.LogInfo($"{ModuleTag} Invoke skipped: method={(method != null)} manager={(manager != null)}.");
                }
                return;
            }

            try
            {
                method.Invoke(manager, new object[] { spot });
                if (InternalDebugConfig.DebugAbilityInputFlow
                    && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                    && LogLimiter.Allow("AbilityFlow.InvokeOk", 0.15f))
                {
                    Log.LogInfo($"{ModuleTag} Invoke ok: {method.Name} spotAbility={(spot.CurrentAbility?.GetType().Name ?? "none")}.");
                }
            }
            catch (Exception ex)
            {
                if (InternalDebugConfig.DebugAbilityInputFlow
                    && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                    && LogLimiter.Allow("AbilityFlow.InvokeFail", 0.25f))
                {
                    var root = ex;
                    if (ex is TargetInvocationException tie && tie.InnerException != null)
                    {
                        root = tie.InnerException;
                    }

                    var target = root.TargetSite != null
                        ? $"{root.TargetSite.DeclaringType?.FullName}.{root.TargetSite.Name}"
                        : "unknown";
                    var stack = string.IsNullOrEmpty(root.StackTrace) ? "none" : root.StackTrace;
                    Log.LogInfo(
                        $"{ModuleTag} Invoke fail: {method.Name} " +
                        $"wrapper={ex.GetType().Name}: {ex.Message} " +
                        $"root={root.GetType().Name}: {root.Message} target={target} stack={stack}");
                }
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
            foreach (var key in CustomInputActions.Keys.ToArray())
            {
                DisposeCustomInputAction(key);
            }

            _instance = null;
        }

        private static void DisposeCustomInputAction(InputKey key)
        {
            if (!CustomInputActions.TryGetValue(key, out var existing))
            {
                return;
            }

            try
            {
                existing.Action.Disable();
                existing.Action.Dispose();
            }
            catch
            {
            }

            CustomInputActions.Remove(key);
        }

        private static void LogInputFlow(string key, string message, float interval = 0.5f)
        {
            if (!ShouldLogAbilityFlow($"AbilityFlow.{key}", interval))
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        private static void LogCoreFlow(string key, string message, float interval = 0.5f)
        {
            if (!ShouldLogAbilityFlow($"AbilityFlow.{key}", interval))
            {
                return;
            }

            Log.LogInfo($"{ModuleTag} {message}");
        }

        internal static bool IsDebugBurstLoggingActive()
        {
            return IsDebugBurstActive();
        }

        private static bool IsDebugBurstActive()
        {
            return InternalDebugConfig.DebugAbilityInputFlow
                && SpectateHeadBridge.IsLocalDeathHeadTriggered()
                && Time.realtimeSinceStartup <= s_debugBurstUntilTime;
        }

        private static bool ShouldLogAbilityFlow(string key, float interval)
        {
            return IsDebugBurstActive() && LogLimiter.Allow(key, interval);
        }

        private static bool IsAbilityRuntimeContextActive()
        {
            // Ability input must stay available while spectating DHH head context, even when
            // some runtime flags are temporarily desynced across vanilla/DHH states.
            var active = SpectateHeadBridge.IsDhhRuntimeInputContextActive()
                || SpectateHeadBridge.IsLocalDeathHeadTriggered()
                || SpectateHeadBridge.IsSpectatingHead();
            if (active)
            {
                EnsureAttachedRuntime();
            }

            return active;
        }

        private static int GetDirectionSlotIndex()
        {
            var configuredSlot = Mathf.Clamp(FeatureFlags.AbilityDirectionSlot, 2, SlotCount);
            return configuredSlot - 1;
        }
    }
}
