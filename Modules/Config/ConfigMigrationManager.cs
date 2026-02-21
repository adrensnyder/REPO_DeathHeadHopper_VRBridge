using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;

namespace DeathHeadHopperVRBridge.Modules.Config
{
    internal static class ConfigMigrationManager
    {
        private readonly struct FileEntry
        {
            public FileEntry(int lineIndex, string section, string key, string value)
            {
                LineIndex = lineIndex;
                Section = section;
                Key = key;
                Value = value;
            }

            public int LineIndex { get; }
            public string Section { get; }
            public string Key { get; }
            public string Value { get; }
        }

        internal static void Apply(ConfigFile config, Type flagsType, string defaultSection)
        {
            if (config == null ||
                flagsType == null ||
                string.IsNullOrWhiteSpace(config.ConfigFilePath) ||
                !File.Exists(config.ConfigFilePath))
            {
                return;
            }

            var lines = File.ReadAllLines(config.ConfigFilePath);
            if (lines.Length == 0)
            {
                return;
            }

            var entries = ParseEntries(lines);
            if (entries.Count == 0)
            {
                return;
            }

            var activeDefinitions = new HashSet<ConfigDefinition>(config.Keys);
            if (activeDefinitions.Count == 0)
            {
                return;
            }

            var aliases = BuildAliasMap(flagsType, defaultSection);
            var activeDefinitionsByKey = BuildActiveByKey(activeDefinitions);
            var orphanedLineIndexes = new HashSet<int>();

            foreach (var entry in entries)
            {
                var oldDefinition = new ConfigDefinition(entry.Section, entry.Key);
                if (activeDefinitions.Contains(oldDefinition))
                {
                    continue;
                }

                var migrationApplied = false;
                if (aliases.TryGetValue(oldDefinition, out var mappedDefinition))
                {
                    migrationApplied = TryMigrateValue(config, activeDefinitions, mappedDefinition, entry.Value);
                }
                else if (activeDefinitionsByKey.TryGetValue(entry.Key, out var candidates) && candidates.Count == 1)
                {
                    var candidate = candidates[0];
                    if (!Equals(candidate, oldDefinition))
                    {
                        // Generic fallback: same key moved to a single new section.
                        migrationApplied = TryMigrateValue(config, activeDefinitions, candidate, entry.Value);
                    }
                }

                if (migrationApplied || !activeDefinitions.Contains(oldDefinition))
                {
                    orphanedLineIndexes.Add(entry.LineIndex);
                }
            }

            if (orphanedLineIndexes.Count == 0)
            {
                return;
            }

            var cleanedLines = new List<string>(lines.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!orphanedLineIndexes.Contains(i))
                {
                    cleanedLines.Add(lines[i]);
                }
            }

            File.WriteAllLines(config.ConfigFilePath, cleanedLines.ToArray());
            config.Reload();
        }

        private static bool TryMigrateValue(
            ConfigFile config,
            HashSet<ConfigDefinition> activeDefinitions,
            ConfigDefinition destination,
            string serializedValue)
        {
            if (!activeDefinitions.Contains(destination) || !config.ContainsKey(destination))
            {
                return false;
            }

            var destinationEntry = config[destination];
            if (destinationEntry == null)
            {
                return false;
            }

            try
            {
                destinationEntry.SetSerializedValue(serializedValue);
                return true;
            }
            catch
            {
                // Ignore invalid legacy payloads and keep current bound value.
                return false;
            }
        }

        private static Dictionary<ConfigDefinition, ConfigDefinition> BuildAliasMap(Type flagsType, string defaultSection)
        {
            var aliases = new Dictionary<ConfigDefinition, ConfigDefinition>();

            foreach (var field in flagsType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var entryAttribute = field.GetCustomAttribute<FeatureConfigEntryAttribute>();
                if (entryAttribute == null)
                {
                    continue;
                }

                var currentSection = string.IsNullOrWhiteSpace(entryAttribute.Section)
                    ? defaultSection
                    : entryAttribute.Section;
                var currentKey = string.IsNullOrWhiteSpace(entryAttribute.Key)
                    ? field.Name
                    : entryAttribute.Key;
                var currentDefinition = new ConfigDefinition(currentSection, currentKey);

                var aliasAttributes = field.GetCustomAttributes<FeatureConfigAliasAttribute>();
                foreach (var alias in aliasAttributes)
                {
                    if (alias == null || string.IsNullOrWhiteSpace(alias.OldKey))
                    {
                        continue;
                    }

                    var aliasSection = string.IsNullOrWhiteSpace(alias.OldSection)
                        ? defaultSection
                        : alias.OldSection;
                    var aliasDefinition = new ConfigDefinition(aliasSection, alias.OldKey);
                    aliases[aliasDefinition] = currentDefinition;
                }
            }

            return aliases;
        }

        private static Dictionary<string, List<ConfigDefinition>> BuildActiveByKey(HashSet<ConfigDefinition> activeDefinitions)
        {
            var byKey = new Dictionary<string, List<ConfigDefinition>>(StringComparer.Ordinal);

            foreach (var definition in activeDefinitions)
            {
                if (!byKey.TryGetValue(definition.Key, out var items))
                {
                    items = new List<ConfigDefinition>();
                    byKey[definition.Key] = items;
                }

                items.Add(definition);
            }

            return byKey;
        }

        private static List<FileEntry> ParseEntries(string[] lines)
        {
            var entries = new List<FileEntry>();
            var currentSection = string.Empty;

            for (var i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var line = rawLine.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Length >= 3 && line[0] == '[' && line[line.Length - 1] == ']')
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                var value = line.Substring(separatorIndex + 1).Trim();
                entries.Add(new FileEntry(i, currentSection, key, value));
            }

            return entries;
        }
    }
}

