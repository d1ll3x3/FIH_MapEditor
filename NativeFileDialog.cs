using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FIHMapEditor
{
    // Windows "Open file" dialog for picking a voice-line clip from anywhere on the PC.
    // IMGUI has no file picker under IL2CPP, so this calls comdlg32 directly (no extra
    // dependency). The dialog blocks the thread that shows it, so it runs on its own STA
    // thread and the game keeps rendering; the result is handed to a callback on that thread,
    // which must only marshal it to the main thread — no Unity/IL2CPP calls from there.
    public static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName(ref OpenFileName ofn);

        [DllImport("comdlg32.dll")]
        private static extern int CommDlgExtendedError();

        private const int OFN_NOCHANGEDIR = 0x00000008;   // keep the game's working directory
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_EXPLORER = 0x00080000;
        private const int MAX_PATH_CHARS = 4096;

        // onDone(path, error): path is null when cancelled or on failure; error is non-null
        // only on failure. Called on the dialog's thread.
        public static void OpenAudioFile(Action<string, string> onDone)
        {
            if (!OperatingSystem.IsWindows())
            {
                onDone(null, "the file dialog needs Windows");
                return;
            }
            var thread = new Thread(() =>
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    buffer = Marshal.AllocHGlobal(MAX_PATH_CHARS * 2);
                    Marshal.WriteInt16(buffer, 0);
                    var ofn = new OpenFileName
                    {
                        lStructSize = Marshal.SizeOf<OpenFileName>(),
                        // No owner window: owning the game's window from another thread would
                        // tie the dialog to the render thread's input queue and can stall it.
                        hwndOwner = IntPtr.Zero,
                        lpstrFilter = "Audio (*.ogg;*.mp3;*.wav;*.flac)\0*.ogg;*.mp3;*.wav;*.flac\0\0",
                        nFilterIndex = 1,
                        lpstrFile = buffer,
                        nMaxFile = MAX_PATH_CHARS,
                        lpstrTitle = "Choose a voice line (max 1 MB)",
                        Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                    };
                    if (GetOpenFileName(ref ofn))
                    {
                        onDone(Marshal.PtrToStringUni(buffer), null);
                        return;
                    }
                    int code = CommDlgExtendedError();
                    onDone(null, code == 0 ? null : $"file dialog error 0x{code:X}");
                }
                catch (Exception ex)
                {
                    onDone(null, ex.Message);
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            })
            {
                IsBackground = true,   // never keeps the game alive after it quits
                Name = "FIH file dialog",
            };
            thread.SetApartmentState(ApartmentState.STA);   // common dialogs require STA
            thread.Start();
        }
    }
}
