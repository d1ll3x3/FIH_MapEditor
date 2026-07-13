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
        public const int CURRENT_VERSION = 3;

        // No initializer on purpose: an old JSON without this field deserializes to 0,
        // which triggers the migration below.
        public int Version { get; set; }

        public KeyCode ToggleEditorKey { get; set; } = KeyCode.F6;
        // NOT Tab: Tab opens the game's own menu.
        public KeyCode ToggleCursorKey { get; set; } = KeyCode.C;
        public KeyCode MapsHubKey { get; set; } = KeyCode.F7;
        public KeyCode TogglePlayKey { get; set; } = KeyCode.P;
        public KeyCode RestartRunKey { get; set; } = KeyCode.R;
        // These two always combine with Ctrl (Ctrl+Z / Ctrl+S by default), so they can
        // share letters with plain single-key binds without conflicting.
        public KeyCode UndoKey { get; set; } = KeyCode.Z;
        public KeyCode SaveKey { get; set; } = KeyCode.S;
        // No version bump for this addition: a missing JSON field keeps the initializer
        // default, and a migration would wipe the user's other binds.
        // E: interact with cannons/boost pads in play mode (no conflict with fly mode).
        public KeyCode InteractKey { get; set; } = KeyCode.E;

        // Fly-mode movement. Space/E default for up/down; Ctrl is reserved for editor
        // shortcuts (Ctrl+Z/D/S), so avoid it for movement.
        public KeyCode FlyForwardKey { get; set; } = KeyCode.W;
        public KeyCode FlyBackKey { get; set; } = KeyCode.S;
        public KeyCode FlyLeftKey { get; set; } = KeyCode.A;
        public KeyCode FlyRightKey { get; set; } = KeyCode.D;
        public KeyCode FlyUpKey { get; set; } = KeyCode.Space;
        public KeyCode FlyDownKey { get; set; } = KeyCode.Q;
        public KeyCode FlyBoostKey { get; set; } = KeyCode.LeftShift;

        public float FlySpeed { get; set; } = 18.0f;
        public float FlySpeedBoost { get; set; } = 3.0f;
        public float NudgeStep { get; set; } = 0.25f;
        public float AutosaveIntervalSeconds { get; set; } = 30f;
        // Scales the menu window and the HUD together — for high/low-DPI displays where
        // the fixed-pixel layout reads too small or too large. Adjustable from KEYS.
        public float GuiScale { get; set; } = 1.0f;

        // Best run time per map name, in seconds (kept out of the shared map files).
        public Dictionary<string, double> BestTimes { get; set; } = new Dictionary<string, double>();

        // Starred catalog entries (by display name) — survive rescans and sessions.
        public List<string> FavoriteObjects { get; set; } = new List<string>();

        // Global leaderboard backend. Empty = use the values baked into
        // LeaderboardService (the shared backend everyone points at); set these to
        // override with your own Supabase project for testing.
        public string SupabaseUrl { get; set; } = "";
        public string SupabaseAnonKey { get; set; } = "";
        // Upload your own times to the global board. Off = view-only (privacy).
        public bool SubmitTimesOnline { get; set; } = true;

        // Secret owner tokens for maps you've uploaded (map_id -> token). Lets you
        // update/delete your own online maps; losing it just means re-uploading as new.
        public Dictionary<string, string> OwnerTokens { get; set; } = new Dictionary<string, string>();
    }

    public static class EditorConfig
    {
        private static string ConfigFilePath => Path.Combine(Paths.ConfigPath, "com.flippingishard.mapeditor.json");

        public static EditorSettings Settings { get; set; } = new EditorSettings();

        // Clamped read used by every GUI scale site — the raw setting is trusted to be
        // in range (SetUiScale clamps on write), this just guards a hand-edited config.
        public static float UiScale => Mathf.Clamp(Settings.GuiScale, 0.5f, 2f);

        // Gates the per-object log lines ([PLACE]/[GROUND]/[CATALOG]) that repeat dozens
        // of times per map load. Flip to true and rebuild when debugging.
        public static bool VerboseLogs = false;

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
                        // Regenerate keybinds/settings but never lose best times,
                        // favorites or the user's leaderboard backend override.
                        var bestTimes = Settings.BestTimes;
                        var favorites = Settings.FavoriteObjects;
                        var supaUrl = Settings.SupabaseUrl;
                        var supaKey = Settings.SupabaseAnonKey;
                        var submit = Settings.SubmitTimesOnline;
                        var ownerTokens = Settings.OwnerTokens;
                        Settings = new EditorSettings
                        {
                            Version = EditorSettings.CURRENT_VERSION,
                            BestTimes = bestTimes ?? new Dictionary<string, double>(),
                            FavoriteObjects = favorites ?? new List<string>(),
                            SupabaseUrl = supaUrl ?? "",
                            SupabaseAnonKey = supaKey ?? "",
                            SubmitTimesOnline = submit,
                            OwnerTokens = ownerTokens ?? new Dictionary<string, string>(),
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

        // Resets keybinds and tuning values only. User DATA must survive — especially
        // OwnerTokens: losing those permanently orphans the user's uploaded online maps.
        public static void ResetToDefaults()
        {
            var bestTimes = Settings.BestTimes;
            var favorites = Settings.FavoriteObjects;
            var supaUrl = Settings.SupabaseUrl;
            var supaKey = Settings.SupabaseAnonKey;
            var submit = Settings.SubmitTimesOnline;
            var ownerTokens = Settings.OwnerTokens;
            Settings = new EditorSettings
            {
                Version = EditorSettings.CURRENT_VERSION,
                BestTimes = bestTimes ?? new Dictionary<string, double>(),
                FavoriteObjects = favorites ?? new List<string>(),
                SupabaseUrl = supaUrl ?? "",
                SupabaseAnonKey = supaKey ?? "",
                SubmitTimesOnline = submit,
                OwnerTokens = ownerTokens ?? new Dictionary<string, string>(),
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
