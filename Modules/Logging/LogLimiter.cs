#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace DeathHeadHopperVRBridge.Modules.Logging
{
    internal static class LogLimiter
    {
        private static readonly Dictionary<string, float> _next = new();

        internal static bool Allow(string key, float everySeconds)
        {
            var now = Time.realtimeSinceStartup;
            if (_next.TryGetValue(key, out var next) && now < next)
            {
                return false;
            }

            _next[key] = now + everySeconds;
            return true;
        }
    }
}
