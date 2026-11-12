// ============================================================================
//  Install.Folder.cs — часть класса Installer: установка из папки и распаковка rar/7z.
//  Общее описание — в начале Install.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Exuvia
{
    internal static partial class Installer
    {
        // ---- установка из папки ----

        /// Ищет в папке скины обоих форматов и переносит их на место. Возвращает, сколько скинов установлено.
        internal static int InstallFolder(string root, string hint, List<string> installed)
        {
            var markers = new List<string>();
            var packs = new List<string>();
            FindSkins(root, 0, markers, packs);
            int count = 0;
            // Значок и DLL автора ищем до переноса: потом папки разъедутся.
            var dlls = new List<string>();
            try
            {
                foreach (string dll in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
                    dlls.Add(System.IO.Path.GetFileName(dll));
            }
            catch (Exception) { }
            string icon = null;
            foreach (string name in new[] { "icon.png", PreviewFile })
                if (icon == null && File.Exists(System.IO.Path.Combine(root, name))) icon = System.IO.Path.Combine(root, name);
            var homes = new List<string>();

            foreach (string marker in markers)
            {
                if (!HasAnyFile(marker)) continue;
                string parent = System.IO.Path.GetDirectoryName(marker);
                string parentName = System.IO.Path.GetFileName(parent);
                string name;
                if (SamePath(marker, root) || SamePath(parent, root)
                    || parentName.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                    name = HintName(hint);
                else
                    name = CleanName(parentName);
                MergeMove(marker, System.IO.Path.Combine(System.IO.Path.Combine(StitchRoot, name), "Stitchwork"));
                homes.Add(System.IO.Path.Combine(StitchRoot, name));
                installed.Add(name);
                count++;
            }

            foreach (string pack in packs)
            {
                string name = SamePath(pack, root) ? HintName(hint) : CleanName(System.IO.Path.GetFileName(pack));
                // active.txt не переносим: скин включают в окне. Если такой скин уже стоял, его состояние не меняется.
                string marker = System.IO.Path.Combine(pack, "active.txt");
                if (File.Exists(marker)) File.Delete(marker);
                MergeMove(pack, System.IO.Path.Combine(CustomizerRoot, name));
                homes.Add(System.IO.Path.Combine(CustomizerRoot, name));
                installed.Add(name);
                count++;
            }
            foreach (string home in homes)
            {
                try
                {
                    string preview = System.IO.Path.Combine(home, PreviewFile);
                    if (icon != null && homes.Count == 1 && File.Exists(icon) && Directory.Exists(home)) File.Copy(icon, preview, true);
                }
                catch (Exception) { }
                NoteDlls(home, dlls);
            }
            return count;
        }

        static void FindSkins(string dir, int depth, List<string> markers, List<string> packs)
        {
            if (IsMarker(System.IO.Path.GetFileName(dir))) { markers.Add(dir); return; }
            if (IsCustomizerPack(dir)) { packs.Add(dir); return; }
            if (depth >= 4) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (string sub in subs) FindSkins(sub, depth + 1, markers, packs);
        }

        /// Скины ставятся в саму папку Skins, и эта папка уже лежит как готовый скин
        /// (Skins\<имя>\<коллекция>\atlasN.png, Skins\<имя>\Texture2D или Skins\<имя>\Stitchwork).
        internal static bool InPlace(string dir)
        {
            if (SameRoot(CustomizerRoot, SkinsDir) && IsCustomizerPack(dir)) return true;
            if (SameRoot(StitchRoot, SkinsDir))
                foreach (string marker in new[] { "Stitchwork", "Patchwork" })
                    if (NotEmpty(System.IO.Path.Combine(dir, marker))) return true;
            return false;
        }

        /// Готовый скин, который трогать не надо. Папку с версией в имени («Автор-Скин-1.2.3»)
        /// всё же разбираем: она переедет под коротким именем.
        static bool Settled(string dir)
        {
            string name = System.IO.Path.GetFileName(dir);
            return InPlace(dir) && string.Equals(HintName(name), name, StringComparison.OrdinalIgnoreCase);
        }

        static bool SameRoot(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return SamePath(a, b); }
            catch (Exception) { return false; }
        }

        static bool NotEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (string e in Directory.EnumerateFileSystemEntries(dir)) return e != null;
            }
            catch (Exception) { }
            return false;
        }

        /// Убирает пустые папки внутри dir (саму dir не трогает).
        static void RemoveEmptyDirs(string dir)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    RemoveEmptyDirs(sub);
                    if (!NotEmpty(sub)) Directory.Delete(sub, false);
                }
            }
            catch (Exception) { }
        }

        /// Папка скина Customizer: внутри есть папка с atlasN.png или папка Texture2D с картинками.
        internal static bool IsCustomizerPack(string dir)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    bool t2d = System.IO.Path.GetFileName(sub).Equals("Texture2D", StringComparison.OrdinalIgnoreCase);
                    foreach (string f in Directory.GetFiles(sub, "*.png"))
                        if (t2d || AtlasPng.IsMatch(System.IO.Path.GetFileName(f))) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        static bool HasAnyFile(string dir)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) return f != null;
            }
            catch (Exception) { }
            return false;
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'),
                System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        /// Имя скина из имени папки или архива: "Автор-Скин-1.2.3" -> "Скин", остальное как есть.
        static string HintName(string hint)
        {
            return CleanName(VersionTail.IsMatch(hint) ? NameFromZip(hint + ".zip") : hint);
        }

        /// Переносит папку; если на месте уже есть такая — заменяет в ней совпадающие файлы.
        static void MergeMove(string src, string dst)
        {
            // Назначение внутри самой переносимой папки (кинули папку «Stitchwork» как есть):
            // сначала отставляем её в сторону, иначе она переедет сама в себя и пропадёт.
            string s0 = System.IO.Path.GetFullPath(src).TrimEnd('\\', '/');
            string d0 = System.IO.Path.GetFullPath(dst).TrimEnd('\\', '/');
            string parkRoot = null;
            if (d0.StartsWith(s0 + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                parkRoot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s0), "_tmp");
                string park = System.IO.Path.Combine(parkRoot, "move-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(parkRoot);
                Directory.Move(s0, park);
                src = park;
            }
            try { MergeMoveCore(src, dst); }
            finally
            {
                if (parkRoot != null && !NotEmpty(parkRoot))
                    try { Directory.Delete(parkRoot, false); } catch (Exception) { }
            }
        }

        static void MergeMoveCore(string src, string dst)
        {
            if (!Directory.Exists(dst))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                try
                {
                    Directory.Move(src, dst);
                    return;
                }
                catch (Exception) { } // например, другой диск: переносим по файлу
            }
            string from = System.IO.Path.GetFullPath(src).TrimEnd('\\', '/');
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(from.Length + 1);
                string dest = System.IO.Path.Combine(dst, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
            DeleteTree(from);
        }

        /// Убирает разобранный архив (или остатки папки) в Skins\_installed, чтобы он не ставился заново.
        static void MoveToDone(string path)
        {
            Directory.CreateDirectory(DoneDir);
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            bool dir = Directory.Exists(path);
            if (dir) { stem = name; ext = ""; }
            string dest = System.IO.Path.Combine(DoneDir, name);
            for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
                dest = System.IO.Path.Combine(DoneDir, stem + " (" + i + ")" + ext);
            if (dir) Directory.Move(path, dest); else File.Move(path, dest);
        }

        static void DeleteTree(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { }
        }

        /// Распаковка rar/7z штатным tar.exe из Windows. Возвращает текст ошибки или null.
        static string Untar(string archive, string dest)
        {
            string advice = Say(" Перепакуй скин в zip или распакуй сам и кинь в Skins папку.",
                " Repack the skin as a zip, or unpack it yourself and drop the folder into Skins.");
            string tar = TarPath;
            if (tar.Length == 0)
            {
                tar = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                if (!File.Exists(tar)) return Say("этот вид архива открыть нечем (в Windows нет tar.exe).",
                    "this archive type cannot be opened (Windows has no tar.exe).") + advice;
            }
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = tar;
                psi.Arguments = "-xf \"" + archive + "\" -C \"" + dest + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process process = System.Diagnostics.Process.Start(psi))
                {
                    if (!process.WaitForExit(180000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return Say("распаковка шла дольше трёх минут и остановлена.",
                            "unpacking took more than three minutes and was stopped.") + advice;
                    }
                    if (process.ExitCode != 0) return Say("Windows не смог распаковать этот архив (код ",
                        "Windows could not unpack this archive (code ") + process.ExitCode + ")." + advice;
                }
                return null;
            }
            catch (Exception e)
            {
                return Say("не удалось запустить распаковку: ", "could not start unpacking: ") + e.Message + "."
                    + advice;
            }
        }
    }
}
