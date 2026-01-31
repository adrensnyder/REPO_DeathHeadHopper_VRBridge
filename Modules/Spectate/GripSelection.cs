#nullable enable

using System;
using RepoXR.Managers;

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal enum GripSelection
    {
        Auto,
        Left,
        Right
    }

    internal static class GripSelectionHelper
    {
        internal static GripSelection Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return GripSelection.Auto;
            }

            if (Enum.TryParse(raw.Trim(), true, out GripSelection parsed))
            {
                return parsed;
            }

            return GripSelection.Auto;
        }

        internal static bool ShouldUseLeft(GripSelection selection)
        {
            return selection switch
            {
                GripSelection.Left => true,
                GripSelection.Right => false,
                _ => !VRSession.IsLeftHanded,
            };
        }
    }
}
