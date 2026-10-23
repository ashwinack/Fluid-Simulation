using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FluidSim
{
    // The background image library behind the BACKGROUND dialog: the images built
    // into the app, plus any the operator uploads. Uploads are copied into a
    // "Backgrounds" folder beside the settings files, so the app keeps working if
    // the original is moved or the USB stick is pulled. The choice is stored in
    // revealImagePath:
    //   ""                  - revealTexture, the image assigned in the scene
    //   "builtin:<name>"    - one of builtInBackgrounds
    //   "Backgrounds/x.jpg" - an upload (relative = under StreamingAssets)
    //   "C:\...\x.jpg"      - an upload in the fallback folder, or any file path
    public partial class FluidSimulation
    {
        public const string BuiltInPrefix = "builtin:";
        public const string BackgroundsFolderName = "Backgrounds";

        [Tooltip("More images built into the app, offered in the BACKGROUND dialog next to revealTexture.")]
        public Texture2D[] builtInBackgrounds = new Texture2D[0];

        /// <summary>One image the operator can pick.</summary>
        public class BackgroundOption
        {
            public string key;          // the revealImagePath that selects it
            public string name;
            public bool builtIn;
            public Texture texture;     // built-in images only
            public string file;         // uploads only: the full path
        }

        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

        Texture FindBuiltInBackground(string name)
        {
            if (revealTexture != null && revealTexture.name == name) return revealTexture;
            if (builtInBackgrounds != null)
                foreach (var t in builtInBackgrounds)
                    if (t != null && t.name == name) return t;
            return null;
        }

        static string StreamingBackgrounds => Path.Combine(StreamingDir, BackgroundsFolderName);
        static string FallbackBackgrounds => Path.Combine(FallbackDir, BackgroundsFolderName);

        string backgroundsFolder;

        /// <summary>
        /// Where uploads are copied: StreamingAssets/Backgrounds beside the settings,
        /// or the user data folder when the install folder is read-only.
        /// </summary>
        public string BackgroundsFolder
        {
            get
            {
                if (backgroundsFolder != null) return backgroundsFolder;
                foreach (string dir in new[] { StreamingBackgrounds, FallbackBackgrounds })
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string probe = Path.Combine(dir, ".write-test");
                        File.WriteAllText(probe, "");
                        File.Delete(probe);
                        return backgroundsFolder = dir;
                    }
                    catch (System.Exception) { /* read-only: try the next one */ }
                }
                return backgroundsFolder = FallbackBackgrounds;
            }
        }

        /// <summary>The revealImagePath that selects this file.</summary>
        static string KeyForFile(string fullPath)
        {
            string full = Path.GetFullPath(fullPath);
            string streaming = Path.GetFullPath(StreamingDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(streaming, System.StringComparison.OrdinalIgnoreCase))
                return full.Substring(streaming.Length).Replace('\\', '/');
            return full;
        }

        static string FileForKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.StartsWith(BuiltInPrefix)) return null;
            return Path.IsPathRooted(key) ? key : Path.Combine(StreamingDir, key);
        }

        static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return System.Array.IndexOf(ImageExtensions, ext) >= 0;
        }

        /// <summary>Every image on offer: built-in first, then uploads by name.</summary>
        public List<BackgroundOption> ListBackgrounds()
        {
            var list = new List<BackgroundOption>();
            // Asset names keep a stray ".jpg" when the file was "x.jpg.jpeg"; not shown.
            if (revealTexture != null)
                list.Add(new BackgroundOption { key = "", name = Path.GetFileNameWithoutExtension(revealTexture.name), builtIn = true, texture = revealTexture });
            if (builtInBackgrounds != null)
                foreach (var t in builtInBackgrounds)
                {
                    if (t == null || t == revealTexture) continue;
                    if (list.Exists(o => o.builtIn && o.texture == t)) continue;
                    list.Add(new BackgroundOption { key = BuiltInPrefix + t.name, name = Path.GetFileNameWithoutExtension(t.name), builtIn = true, texture = t });
                }

            var files = new List<string>();
            foreach (string dir in new[] { StreamingBackgrounds, FallbackBackgrounds })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir))
                        if (IsImageFile(f)) files.Add(Path.GetFullPath(f));
                }
                catch (System.Exception e) { Debug.LogWarning($"[FluidSimulation] Could not list {dir}: {e.Message}"); }
            }
            // The current file, if it lives somewhere else (typed in, or an older config).
            string current = FileForKey((revealImagePath ?? "").Trim());
            if (current != null && File.Exists(current)) files.Add(Path.GetFullPath(current));

            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), System.StringComparison.OrdinalIgnoreCase));
            foreach (string f in files)
            {
                if (!seen.Add(f)) continue;
                list.Add(new BackgroundOption { key = KeyForFile(f), name = DisplayName(f), file = f });
            }
            return list;
        }

        /// <summary>"photo.jpg.jpeg" -> "photo": image extensions are not shown.</summary>
        static string DisplayName(string file)
        {
            string name = Path.GetFileName(file);
            while (IsImageFile(name)) name = Path.GetFileNameWithoutExtension(name);
            return name;
        }

        /// <summary>True when this option is the image on the wall now.</summary>
        public bool IsCurrentBackground(BackgroundOption o)
        {
            string key = (revealImagePath ?? "").Trim();
            if (o.file == null) return key == o.key;
            string file = FileForKey(key);
            return file != null && string.Equals(Path.GetFullPath(file), o.file, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Show this image behind the fluid now (not saved).</summary>
        public void SelectBackground(BackgroundOption o)
        {
            revealImagePath = o.key;
            loadedRevealPath = null;    // re-read even if the same path, in case the file changed
        }

        /// <summary>
        /// Copy an image into the Backgrounds folder and show it. The original is only
        /// read. A file of the same name and size is taken to be the same image and
        /// reused rather than copied twice.
        /// </summary>
        public bool ImportBackground(string source, out string message)
        {
            source = (source ?? "").Trim().Trim('"');
            if (source.Length == 0) { message = "No file chosen."; return false; }
            if (!File.Exists(source)) { message = $"File not found: {source}"; return false; }
            if (!IsImageFile(source)) { message = "Only PNG and JPG images can be used."; return false; }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(source); }
            catch (System.Exception e) { message = $"Could not read the file: {e.Message}"; return false; }

            var probe = new Texture2D(2, 2);
            bool ok = probe.LoadImage(bytes, false);
            int w = probe.width, h = probe.height;
            Destroy(probe);
            if (!ok) { message = "That file is not a readable PNG or JPG image."; return false; }

            string folder = BackgroundsFolder;
            string dest;
            try
            {
                Directory.CreateDirectory(folder);
                string stem = Path.GetFileNameWithoutExtension(source), ext = Path.GetExtension(source).ToLowerInvariant();
                dest = Path.Combine(folder, stem + ext);
                bool inFolder = string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), System.StringComparison.OrdinalIgnoreCase);
                for (int n = 2; !inFolder && File.Exists(dest) && new FileInfo(dest).Length != bytes.Length; n++)
                    dest = Path.Combine(folder, $"{stem} ({n}){ext}");
                if (!inFolder && !File.Exists(dest)) File.WriteAllBytes(dest, bytes);
            }
            catch (System.Exception e) { message = $"Could not copy the image into {folder}: {e.Message}"; return false; }

            revealImagePath = KeyForFile(dest);
            loadedRevealPath = null;
            message = $"{Path.GetFileName(dest)} ({w}x{h}) is on the wall now.";
            return true;
        }

        /// <summary>Delete an uploaded copy from the Backgrounds folder. Built-in images cannot be removed.</summary>
        public bool RemoveBackground(BackgroundOption o, out string message)
        {
            if (o.builtIn || o.file == null) { message = "Built-in images cannot be removed."; return false; }
            string dir = Path.GetFullPath(Path.GetDirectoryName(o.file) ?? "").TrimEnd('\\', '/');
            bool inLibrary = string.Equals(dir, Path.GetFullPath(StreamingBackgrounds).TrimEnd('\\', '/'), System.StringComparison.OrdinalIgnoreCase)
                          || string.Equals(dir, Path.GetFullPath(FallbackBackgrounds).TrimEnd('\\', '/'), System.StringComparison.OrdinalIgnoreCase);
            bool wasCurrent = IsCurrentBackground(o);
            if (inLibrary)
            {
                try { File.Delete(o.file); }
                catch (System.Exception e) { message = $"Could not remove it: {e.Message}"; return false; }
            }
            if (wasCurrent)
            {
                revealImagePath = "";
                loadedRevealPath = null;
            }
            message = inLibrary
                ? $"Removed {Path.GetFileName(o.file)}" + (wasCurrent ? " - back to the built-in image." : ".")
                : "That image is outside the Backgrounds folder, so it was only taken off the wall, not deleted.";
            return true;
        }

        /// <summary>Save the image and spotlight settings into fluid-settings.json - the BACKGROUND dialog's Save.</summary>
        public bool SaveBackgroundSettings() => SavePanelPatch(p =>
        {
            p.revealMode = revealMode;
            p.revealImagePath = revealImagePath;
            p.revealFit = (int)revealFit;
            p.spotlightSize = spotlightSize;
            p.spotlightSoftness = spotlightSoftness;
            p.revealBackground = revealBackground;
            p.revealFluidOpacity = revealFluidOpacity;
        });
    }
}
