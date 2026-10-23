using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace FluidSim
{
    /// <summary>
    /// The standard Windows "Open" dialog, for choosing a background image to upload.
    /// It runs on its own thread so the fluid keeps animating on the wall while it
    /// is open; the chosen path is handed back on the main thread by Poll().
    /// In the editor Unity's own file panel is used instead.
    /// </summary>
    public static class NativeFileDialog
    {
        public static bool Supported =>
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
            true;
#else
            false;
#endif

        static volatile bool busy;
        static volatile bool done;
        static string result;
        static Action<string> pending;

        public static bool IsOpen => busy;

        /// <summary>
        /// Open the dialog. <paramref name="onChosen"/> is called on the main thread
        /// (from Poll) with the chosen path, or null if it was cancelled.
        /// </summary>
        public static void OpenImage(string title, string initialFolder, Action<string> onChosen)
        {
            if (busy) return;
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFilePanelWithFilters(title, initialFolder ?? "",
                new[] { "Images", "png,jpg,jpeg" });
            onChosen?.Invoke(string.IsNullOrEmpty(path) ? null : path);
#elif UNITY_STANDALONE_WIN
            pending = onChosen;
            busy = true;
            done = false;
            IntPtr owner = GetActiveWindow();
            var thread = new Thread(() =>
            {
                string chosen = null;
                try { chosen = ShowOpenDialog(owner, title, initialFolder); }
                catch (Exception e) { Debug.LogWarning("[NativeFileDialog] " + e.Message); }
                result = chosen;
                done = true;
            }) { IsBackground = true, Name = "Open image dialog" };
            try { thread.SetApartmentState(ApartmentState.STA); } catch (Exception) { /* not supported: MTA still works */ }
            thread.Start();
#else
            onChosen?.Invoke(null);
#endif
        }

        /// <summary>Call every frame: delivers the dialog's result once it closes.</summary>
        public static void Poll()
        {
            if (!busy || !done) return;
            busy = false;
            done = false;
            var callback = pending;
            pending = null;
            callback?.Invoke(result);
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class OpenFileName
        {
            public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
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

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW([In, Out] OpenFileName ofn);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        const int OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;
        const int MaxPath = 4096;

        static string ShowOpenDialog(IntPtr owner, string title, string initialFolder)
        {
            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * 2);
            try
            {
                Marshal.Copy(new byte[MaxPath * 2], 0, buffer, MaxPath * 2);
                var ofn = new OpenFileName
                {
                    hwndOwner = owner,
                    lpstrFilter = "Images (*.png, *.jpg, *.jpeg)\0*.png;*.jpg;*.jpeg\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = MaxPath,
                    lpstrInitialDir = initialFolder,
                    lpstrTitle = title,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                };
                if (!GetOpenFileNameW(ofn)) return null;
                string path = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
#endif
    }
}
