#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace DeathHeadHopperVRBridge.Modules.Config
{
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class FeatureConfigEntryAttribute : Attribute
    {
        public FeatureConfigEntryAttribute(string section, string description)
        {
            Section = section;
            Description = description;
        }

        public string Section { get; }
        public string Description { get; }
        public string Key { get; set; } = string.Empty;
        public float Min { get; set; } = float.NaN;
        public float Max { get; set; } = float.NaN;
        public float Step { get; set; } = float.NaN;
        public string[] AcceptableValues { get; set; } = Array.Empty<string>();

        public bool HasRange => !float.IsNaN(Min) && !float.IsNaN(Max);
    }

    internal static class ConfigManager
    {
        private struct RangeF { public float Min, Max, Step; }
        private struct RangeI { public int Min, Max, Step; }

        private static bool s_initialized;
        private static readonly char[] ColorSeparators = { ',', ';' };
        private static readonly Dictionary<string, RangeF> s_floatRanges = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, RangeI> s_intRanges = new(StringComparer.Ordinal);

        internal static void Initialize(ConfigFile config)
        {
            if (s_initialized || config == null)
            {
                return;
            }

            s_initialized = true;
            BindConfigEntries(config, typeof(FeatureFlags), "General");
        }

        private static void BindConfigEntries(ConfigFile config, Type targetType, string defaultSection)
        {
            foreach (var field in targetType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attribute = field.GetCustomAttribute<FeatureConfigEntryAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var section = string.IsNullOrWhiteSpace(attribute.Section) ? defaultSection : attribute.Section;
                var key = string.IsNullOrWhiteSpace(attribute.Key) ? field.Name : attribute.Key;
                var description = attribute.Description ?? string.Empty;

                if (field.FieldType == typeof(bool))
                {
                    var defaultValue = (bool)field.GetValue(null)!;
                    var entry = config.Bind(section, key, defaultValue, description);
                    ApplyAndWatch(entry, value => field.SetValue(null, value));
                    continue;
                }

                if (field.FieldType == typeof(int))
                {
                    var defaultValue = (int)field.GetValue(null)!;
                    ConfigEntry<int> entry;
                    if (attribute.HasRange)
                    {
                        var min = GetIntRangeStart(attribute);
                        var max = GetIntRangeEnd(attribute);
                        entry = config.Bind(section, key, defaultValue,
                            new ConfigDescription(description, new AcceptableValueRange<int>(min, max)));
                        RegisterIntRange(key, min, max, DetermineIntStep(attribute));
                    }
                    else
                    {
                        entry = config.Bind(section, key, defaultValue, description);
                    }

                    ApplyAndWatch(entry, value => field.SetValue(null, value));
                    continue;
                }

                if (field.FieldType == typeof(float))
                {
                    var defaultValue = (float)field.GetValue(null)!;
                    ConfigEntry<float> entry;
                    if (attribute.HasRange)
                    {
                        var min = Math.Min(attribute.Min, attribute.Max);
                        var max = Math.Max(attribute.Min, attribute.Max);
                        entry = config.Bind(section, key, defaultValue,
                            new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
                        RegisterFloatRange(key, min, max, DetermineFloatStep(attribute, min));
                    }
                    else
                    {
                        entry = config.Bind(section, key, defaultValue, description);
                    }

                    ApplyAndWatch(entry, value => field.SetValue(null, value));
                    continue;
                }

                if (field.FieldType == typeof(string))
                {
                    var defaultValue = field.GetValue(null) as string ?? string.Empty;
                    var entry = config.Bind(section, key, defaultValue, BuildStringDescription(description, attribute.AcceptableValues));
                    ApplyAndWatch(entry, value => field.SetValue(null, value));
                    continue;
                }

                if (field.FieldType == typeof(Color))
                {
                    var defaultValue = (Color)field.GetValue(null)!;
                    var entry = config.Bind(section, key, ColorToString(defaultValue), description);
                    ApplyAndWatch(entry, ColorFromString, value => field.SetValue(null, value));
                    continue;
                }
            }
        }

        private static int GetIntRangeStart(FeatureConfigEntryAttribute attribute)
        {
            ValidateIntegerRange(attribute);
            return (int)Math.Min(attribute.Min, attribute.Max);
        }

        private static int GetIntRangeEnd(FeatureConfigEntryAttribute attribute)
        {
            ValidateIntegerRange(attribute);
            return (int)Math.Max(attribute.Min, attribute.Max);
        }

        private static void ValidateIntegerRange(FeatureConfigEntryAttribute attribute)
        {
            if (!IsWholeNumber(attribute.Min) || !IsWholeNumber(attribute.Max))
            {
                throw new InvalidOperationException("FeatureConfigEntryAttribute integer range values must be whole numbers.");
            }
        }

        private static bool IsWholeNumber(float value)
        {
            double truncated = Math.Truncate(value);
            return Math.Abs(value - truncated) < float.Epsilon;
        }

        private static void ApplyAndWatch<T>(ConfigEntry<T> entry, Action<T> setter)
        {
            if (entry == null || setter == null)
            {
                return;
            }

            void Update()
            {
                setter(SanitizeValue(entry.Value, entry.Definition.Key));
            }

            Update();
            entry.SettingChanged += (_, _) => Update();
        }

        private static void ApplyAndWatch(ConfigEntry<string> entry, Func<string, Color> parser, Action<Color> setter)
        {
            if (entry == null || parser == null || setter == null)
            {
                return;
            }

            setter(parser(entry.Value));
            entry.SettingChanged += (_, _) => setter(parser(entry.Value));
        }

        private static string ColorToString(Color input)
        {
            return string.Join(",",
                input.r.ToString(CultureInfo.InvariantCulture),
                input.g.ToString(CultureInfo.InvariantCulture),
                input.b.ToString(CultureInfo.InvariantCulture),
                input.a.ToString(CultureInfo.InvariantCulture));
        }

        private static Color ColorFromString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Color.black;
            }

            var segments = input.Split(ColorSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return Color.black;
            }

            float r = 0f, g = 0f, b = 0f, a = 1f;
            TryParseComponent(segments, 0, ref r);
            TryParseComponent(segments, 1, ref g);
            TryParseComponent(segments, 2, ref b);
            TryParseComponent(segments, 3, ref a);

            return new Color(r, g, b, a);
        }

        private static void TryParseComponent(string[] segments, int index, ref float slot)
        {
            if (index >= segments.Length)
            {
                return;
            }

            var trimmed = segments[index].Trim();
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                slot = parsed;
            }
        }

        private static T SanitizeValue<T>(T value, string key)
        {
            if (value is float f && s_floatRanges.TryGetValue(key, out var floatRange))
            {
                var clamped = Math.Min(floatRange.Max, Math.Max(floatRange.Min, f));
                if (floatRange.Step > 0f)
                {
                    clamped = SnapFloatToStep(clamped, floatRange.Min, floatRange.Step);
                    clamped = Math.Min(floatRange.Max, Math.Max(floatRange.Min, clamped));
                }

                return (T)(object)clamped;
            }

            if (value is int i && s_intRanges.TryGetValue(key, out var intRange))
            {
                var clamped = Math.Min(intRange.Max, Math.Max(intRange.Min, i));
                if (intRange.Step > 0)
                {
                    clamped = SnapIntToStep(clamped, intRange.Min, intRange.Step);
                    clamped = Math.Min(intRange.Max, Math.Max(intRange.Min, clamped));
                }

                return (T)(object)clamped;
            }

            return value;
        }

        private static ConfigDescription BuildStringDescription(string description, string[] acceptableValues)
        {
            if (acceptableValues == null || acceptableValues.Length == 0)
            {
                return new ConfigDescription(description);
            }

            return new ConfigDescription(description, new AcceptableValueList<string>(acceptableValues));
        }

        private static int DetermineIntStep(FeatureConfigEntryAttribute attribute)
        {
            if (!float.IsNaN(attribute.Step) && attribute.Step >= 1f)
            {
                return Math.Max(1, (int)Math.Round(attribute.Step));
            }

            return 1;
        }

        private static float DetermineFloatStep(FeatureConfigEntryAttribute attribute, float minValue)
        {
            if (!float.IsNaN(attribute.Step) && attribute.Step > 0f)
            {
                return attribute.Step;
            }

            return DetermineDefaultFloatStep(minValue);
        }

        private static float DetermineDefaultFloatStep(float minValue)
        {
            var baseValue = Math.Abs(minValue);
            if (baseValue <= 0f)
            {
                return 0.1f;
            }

            var exponent = Math.Floor(Math.Log10(baseValue));
            return (float)Math.Pow(10, exponent);
        }

        private static void RegisterFloatRange(string key, float min, float max, float step)
        {
            s_floatRanges[key] = new RangeF
            {
                Min = min,
                Max = max,
                Step = step
            };
        }

        private static void RegisterIntRange(string key, int min, int max, int step)
        {
            s_intRanges[key] = new RangeI
            {
                Min = min,
                Max = max,
                Step = step
            };
        }

        private static float SnapFloatToStep(float value, float min, float step)
        {
            if (step <= 0f)
            {
                return value;
            }

            var offset = (value - min) / step;
            var steps = Math.Round(offset, MidpointRounding.AwayFromZero);
            return min + (float)steps * step;
        }

        private static int SnapIntToStep(int value, int min, int step)
        {
            if (step <= 0)
            {
                return value;
            }

            var offset = (value - min) / (double)step;
            var steps = (int)Math.Round(offset, MidpointRounding.AwayFromZero);
            return min + steps * step;
        }
    }
}
