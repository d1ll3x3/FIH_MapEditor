using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using UnityEngine;

namespace FIHMapEditor
{
    public class EditorSettings
    {
        public const int CURRENT_VERSION = 2;

        // No initializer on purpose: an old JSON without this field deserializes to 0,
        // which triggers the migration below.
        public int Version { get; set; }

        public KeyCode ToggleEditorKey { get; set; } = KeyCode.F6;
        // NOT Tab: Tab opens the game's own menu.
        public KeyCode ToggleCursorKey { get; set; } = KeyCode.C;
        public KeyCode MapsHubKey { get; set; } = KeyCode.F7;
        public KeyCode TogglePlayKey { get; set; } = KeyCode.P;
        public KeyCode RestartRunKey { get; set; } = KeyCode.R;

        public float FlySpeed { get; set; } = 18.0f;
        public float FlySpeedBoost { get; set; } = 3.0f;
        public float NudgeStep { get; set; } = 0.25f;
        public float AutosaveIntervalSeconds { get; set; } = 30f;
        public float GuiScale { get; set; } = 1.0f;

        // Best run time per map name, in seconds (kept out of the shared map files).
        public Dictionary<string, double> BestTimes { get; set; } = new Dictionary<string, double>();
    }

    public static class EditorConfig
    {
        private static string ConfigFilePath => Path.Combine(Paths.ConfigPath, "com.flippingishard.mapeditor.json");

        public static EditorSettings Settings { get; set; } = new EditorSettings();

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                    Settings = JsonSerializer.Deserialize<EditorSettings>(json, options) ?? new EditorSettings();

                    if (Settings.Version < EditorSettings.CURRENT_VERSION)
                    {
                        // Regenerate keybinds/settings but never lose the recorded best times.
                        var bestTimes = Settings.BestTimes;
                        Settings = new EditorSettings
                        {
                            Version = EditorSettings.CURRENT_VERSION,
                            BestTimes = bestTimes ?? new Dictionary<string, double>(),
                        };
                        Save();
                        MapEditorPlugin.Logger.LogInfo($"Config migrated to v{EditorSettings.CURRENT_VERSION}.");
                    }
                }
                else
                {
                    Settings = new EditorSettings { Version = EditorSettings.CURRENT_VERSION };
                    Save(); // Create default config file
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error loading config: {ex.Message}. Using defaults.");
                Settings = new EditorSettings { Version = EditorSettings.CURRENT_VERSION };
            }
        }

        public static void ResetToDefaults()
        {
            var bestTimes = Settings.BestTimes;
            Settings = new EditorSettings
            {
                Version = EditorSettings.CURRENT_VERSION,
                BestTimes = bestTimes ?? new Dictionary<string, double>(),
            };
            Save();
        }

        public static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
                string json = JsonSerializer.Serialize(Settings, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error saving config: {ex.Message}");
            }
        }
    }
}
