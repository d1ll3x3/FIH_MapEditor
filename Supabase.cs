using System;
using System.Net.Http;

namespace FIHMapEditor
{
    // Shared Supabase (PostgREST) endpoint for both the leaderboard and the online map
    // repository. One HttpClient, one place for the baked-in credentials that every copy
    // of the mod points at (so the board and the map library are global). The anon key is
    // public by design — it can only run the whitelisted RPCs and read what RLS allows.
    // A user can override URL/key in the config for a private/test backend.
    public static class Supabase
    {
        // Baked-in shared backend. Empty = "not configured" (features degrade quietly).
        private const string BAKED_URL = "https://vpqecnzxsehfimylwmzz.supabase.co";
        private const string BAKED_ANON_KEY =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InZwcWVjbnp4c2VoZmlteWx3bXp6Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODM0NTE4MjksImV4cCI6MjA5OTAyNzgyOX0.bk23Gm84QoaBh60aPORnGsJFQvbLdNEguJSXYJOxo7U";

        public static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        public static string Url =>
            (!string.IsNullOrWhiteSpace(EditorConfig.Settings.SupabaseUrl)
                ? EditorConfig.Settings.SupabaseUrl
                : BAKED_URL).TrimEnd('/');

        public static string AnonKey =>
            !string.IsNullOrWhiteSpace(EditorConfig.Settings.SupabaseAnonKey)
                ? EditorConfig.Settings.SupabaseAnonKey
                : BAKED_ANON_KEY;

        public static bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AnonKey);

        // Attach the auth headers every request needs.
        public static void AddAuth(HttpRequestMessage req)
        {
            req.Headers.TryAddWithoutValidation("apikey", AnonKey);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AnonKey);
        }

        public static string Trim(string s)
            => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) : s);
    }
}
