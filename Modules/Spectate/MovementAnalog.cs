#nullable enable

using BepInEx.Logging;
using DeathHeadHopperVRBridge.Modules.Logging;
using DeathHeadHopperVRBridge.Modules.Config;
using Logger = BepInEx.Logging.Logger;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal static class MovementAnalog
    {
        private static readonly ManualLogSource LogSource =
            Logger.CreateLogSource("DeathHeadHopperFix-VR.InputBridge");

        internal const string ModuleTag = "[DeathHeadHopperFix-VR] [SpectateCam]";

    internal static bool TryGetAnalog(out Vector2 movement)
    {
        movement = Vector2.zero;
        InputAction? action;
        try
        {
            action = RepoXR.Input.Actions.Instance["Movement"];
        }
        catch
        {
            action = null;
        }

        if (action == null)
        {
            return false;
        }

        movement = action.ReadValue<Vector2>();
        if (movement.sqrMagnitude > 0.0001f && SpectateHeadBridge.IsSpectatingHead())
        {
            if (FeatureFlags.DebugMovementDirection && LogLimiter.Allow("JoystickSpectateLog", 0.5f))
            {
                LogSource.LogInfo($"{ModuleTag} Joystick moved while spectating head movement={movement:F3}");
            }
        }
        return movement.sqrMagnitude > 0.0001f;
    }

        internal static void LogAnalog(Vector2 analog, bool triggered)
        {
            if (FeatureFlags.DebugMovementDirection && LogLimiter.Allow("MovementAnalog", 0.5f))
            {
                LogSource.LogDebug($"{ModuleTag} Analog movement triggered={triggered} vector={analog:F3}");
            }

            return;
        }

        internal static ManualLogSource Log => LogSource;
    }
}
