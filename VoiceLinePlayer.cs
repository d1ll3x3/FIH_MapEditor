using System.Collections.Generic;

namespace FIHMapEditor
{
    // Plays voice-line clips through the game's own FMOD engine. The game drives all audio
    // with FMOD, not Unity's AudioSource, so clips go into FMOD's voice bus — that way the
    // player's voice volume applies (bus path confirmed on the final-game playtest build).
    // One line at a time: a new line cuts off the previous one instead of talking over it.
    public static class VoiceLinePlayer
    {
        private const string VOICE_BUS = "bus:/VO";

        private static FMOD.Sound _sound;
        private static FMOD.Channel _channel;
        private static bool _voiceBusLocked;
        // Warn once per missing clip: a trigger fires on every entry.
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>();

        public static void Play(string audioHash, string audioName)
        {
            if (string.IsNullOrEmpty(audioHash)) return;
            var clip = VoiceLineLibrary.Find(audioHash);
            if (clip == null)
            {
                if (_warnedMissing.Add(audioHash))
                    MapEditorPlugin.Logger.LogWarning(
                        $"[VOICE] Clip '{audioName}' ({audioHash}) is not in {VoiceLineLibrary.Folder}; trigger is silent.");
                return;
            }

            try
            {
                if (!FMODUnity.RuntimeManager.IsInitialized)
                {
                    MapEditorPlugin.Logger.LogWarning("[VOICE] FMOD is not initialised yet; line not played.");
                    return;
                }

                Stop();
                var core = FMODUnity.RuntimeManager.CoreSystem;
                // Stream instead of loading into memory: voice lines can run for seconds.
                var result = core.createSound(clip.FilePath, FMOD.MODE.CREATESTREAM | FMOD.MODE._2D | FMOD.MODE.LOOP_OFF, out _sound);
                if (result != FMOD.RESULT.OK)
                {
                    MapEditorPlugin.Logger.LogWarning($"[VOICE] createSound failed for '{clip.Name}': {result}");
                    return;
                }

                bool onVoiceBus = TryGetVoiceGroup(out var group);
                result = core.playSound(_sound, group, false, out _channel);
                if (result != FMOD.RESULT.OK)
                    MapEditorPlugin.Logger.LogWarning($"[VOICE] playSound failed for '{clip.Name}': {result}");
                else
                    MapEditorPlugin.Logger.LogInfo($"[VOICE] Playing '{clip.Name}' on {(onVoiceBus ? VOICE_BUS : "the master bus")}.");
            }
            catch (System.Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[VOICE] Playback error for '{clip.Name}': {ex}");
            }
        }

        public static void Stop()
        {
            // stop() on a channel that already finished returns ERR_INVALID_HANDLE — that
            // is the normal case after a clip ends, so its result is intentionally unused.
            if (_channel.hasHandle()) _channel.stop();
            if (_sound.hasHandle()) _sound.release();
            _channel = default;
            _sound = default;
        }

        // A Studio bus only owns a core channel group while it's active; locking it once
        // keeps the group alive, and flushCommands applies the lock before we ask for it.
        // Without the group the line still plays, just on the master bus.
        private static bool TryGetVoiceGroup(out FMOD.ChannelGroup group)
        {
            group = default;
            var studio = FMODUnity.RuntimeManager.StudioSystem;
            var result = studio.getBus(VOICE_BUS, out var bus);
            if (result != FMOD.RESULT.OK)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] Bus '{VOICE_BUS}' not found ({result}); using master.");
                return false;
            }
            if (!_voiceBusLocked)
            {
                bus.lockChannelGroup();
                studio.flushCommands();
                _voiceBusLocked = true;
            }
            result = bus.getChannelGroup(out group);
            if (result != FMOD.RESULT.OK)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] No channel group for '{VOICE_BUS}' ({result}); using master.");
                group = default;
                return false;
            }
            return true;
        }
    }
}
