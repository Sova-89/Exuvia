// ============================================================================
//  Install.Zip.cs — часть класса Installer: установка из zip и мелочи.
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
        // ---- установка из zip ----

        /// Смотрит внутрь архива и решает, что это за скин.
        internal static ArchiveInfo Inspect(string zipPath)
        {
            var info = new ArchiveInfo();
            info.Path = zipPath;
            info.FileName = System.IO.Path.GetFileName(zipPath);
            try
            {
                info.Len = new FileInfo(zipPath).Length;
                var roots = new Dictionary<string, ArchivePack>(StringComparer.OrdinalIgnoreCase);
                var entries = new List<KeyValuePair<string, long>>();

                using (FileStream stream = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name.EndsWith("/")) continue;
                        entries.Add(new KeyValuePair<string, long>(name, entry.Length));
                        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            info.HasDll = true;
                            info.Dlls.Add(name.Substring(name.LastIndexOf('/') + 1));
                        }
                        if (info.Icon == null && name.Equals("icon.png", StringComparison.OrdinalIgnoreCase)) info.Icon = entry.FullName;
                        else if (name.Equals(PreviewFile, StringComparison.OrdinalIgnoreCase)) info.Icon = entry.FullName;
                    }
                }

                // 1) Stitchwork / Patchwork: всё, что лежит под папкой-меткой.
                foreach (KeyValuePair<string, long> e in entries)
                {
                    string[] seg = e.Key.Split('/');
                    for (int i = 0; i < seg.Length - 1; i++)
                    {
                        if (!IsMarker(seg[i])) continue;
                        string prefix = string.Join("/", seg, 0, i + 1) + "/";
                        if (!roots.ContainsKey(prefix))
                        {
                            var pack = new ArchivePack();
                            pack.Kind = SkinKind.Stitch;
                            pack.Prefix = prefix;
                            pack.Marker = seg[i];
                            string parent = i > 0 ? seg[i - 1] : "";
                            pack.Name = (parent.Length == 0
                                || parent.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                                ? NameFromZip(info.FileName) : parent;
                            roots[prefix] = pack;
                        }
                        break;
                    }
                }

                // 2) Customizer: <пак>/<коллекция>/atlasN.png и <пак>/Texture2D/*.png (вне Stitchwork-папок).
                foreach (KeyValuePair<string, long> e in entries)
                {
                    if (UnderStitch(e.Key, roots)) continue;
                    string[] seg = e.Key.Split('/');
                    int n = seg.Length;
                    int rootLen = -1;
                    if (n >= 2 && AtlasPng.IsMatch(seg[n - 1])) rootLen = n - 2;
                    else if (n >= 2 && seg[n - 2].Equals("Texture2D", StringComparison.OrdinalIgnoreCase)
                        && seg[n - 1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) rootLen = n - 2;
                    if (rootLen < 0) continue;

                    string prefix = rootLen == 0 ? "" : string.Join("/", seg, 0, rootLen) + "/";
                    if (roots.ContainsKey("c:" + prefix)) continue;
                    var pack = new ArchivePack();
                    pack.Kind = SkinKind.Customizer;
                    pack.Prefix = prefix;
                    pack.Name = rootLen == 0 ? NameFromZip(info.FileName) : seg[rootLen - 1];
                    roots["c:" + prefix] = pack;
                }

                foreach (ArchivePack pack in roots.Values)
                {
                    pack.Name = CleanName(pack.Name);
                    pack.Target = pack.Kind == SkinKind.Stitch
                        ? System.IO.Path.Combine(System.IO.Path.Combine(StitchRoot, pack.Name), "Stitchwork")
                        : System.IO.Path.Combine(CustomizerRoot, pack.Name);
                    foreach (KeyValuePair<string, long> e in entries)
                    {
                        if (!Belongs(e.Key, pack, roots)) continue;
                        pack.Files++;
                        pack.Bytes += e.Value;
                    }
                    pack.Installed = Directory.Exists(pack.Target);
                    if (pack.Files > 0) info.Packs.Add(pack);
                }
                info.Packs.Sort(delegate (ArchivePack a, ArchivePack b) { return string.Compare(a.Name, b.Name,
                    StringComparison.OrdinalIgnoreCase); });
            }
            catch (Exception e)
            {
                info.Error = e.Message;
            }
            return info;
        }

        /// Распаковывает все скины архива. Возвращает текст ошибки или null.
        internal static string Install(ArchiveInfo info, out int files)
        {
            files = 0;
            if (info.Error != null) return info.Error;
            if (info.Packs.Count == 0) return Say("в архиве не найден скин", "no skin found in the archive");
            try
            {
                var roots = new Dictionary<string, ArchivePack>(StringComparer.OrdinalIgnoreCase);
                foreach (ArchivePack pack in info.Packs)
                    roots[(pack.Kind == SkinKind.Stitch ? "" : "c:") + pack.Prefix] = pack;

                using (FileStream stream = File.OpenRead(info.Path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name.EndsWith("/")) continue;
                        foreach (ArchivePack pack in info.Packs)
                        {
                            if (!Belongs(name, pack, roots)) continue;
                            string rel = name.Substring(pack.Prefix.Length);
                            if (rel.Length == 0) continue;
                            // active.txt не ставим: скин включается в окне, а не сам.
                            if (pack.Kind == SkinKind.Customizer
                                && rel.Equals("active.txt", StringComparison.OrdinalIgnoreCase)) continue;

                            string root = System.IO.Path.GetFullPath(pack.Target);
                            string dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.Replace('/',
                                System.IO.Path.DirectorySeparatorChar)));
                            // защита: файл не должен вылезти из папки скина
                            if (!dest.StartsWith(root + System.IO.Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) continue;

                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                            using (Stream src = entry.Open())
                            using (FileStream dst = File.Create(dest))
                                src.CopyTo(dst);
                            files++;
                            break;
                        }
                    }
                }
                foreach (ArchivePack pack in info.Packs) pack.Installed = Directory.Exists(pack.Target);
                // Значок архива — скину как превью (если в архиве один скин); DLL автора — только записка.
                if (info.Packs.Count == 1) ExtractIcon(info, HomeOf(info.Packs[0]), true);
                foreach (ArchivePack pack in info.Packs) NoteDlls(HomeOf(pack), info.Dlls);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // ---- значок и DLL автора ----

        /// Папка скина: для Stitchwork это папка над «Stitchwork».
        static string HomeOf(ArchivePack pack)
        {
            return pack.Kind == SkinKind.Stitch ? System.IO.Path.GetDirectoryName(pack.Target) : pack.Target;
        }

        static void ExtractIcon(ArchiveInfo info, string home, bool replace)
        {
            if (info.Icon == null || !Directory.Exists(home)) return;
            string dest = System.IO.Path.Combine(home, PreviewFile);
            if (!replace && (File.Exists(dest) || File.Exists(System.IO.Path.Combine(home, "icon.png")))) return;
            try
            {
                using (FileStream stream = File.OpenRead(info.Path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry(info.Icon);
                    if (entry == null) return;
                    using (Stream src = entry.Open())
                    using (FileStream dst = File.Create(dest))
                        src.CopyTo(dst);
                }
            }
            catch (Exception) { }
        }

        /// Запоминает в папке скина, какие DLL автора были в архиве (мы их не ставим: это чужой код).
        static void NoteDlls(string home, List<string> dlls)
        {
            if (dlls == null || dlls.Count == 0 || !Directory.Exists(home)) return;
            try { File.WriteAllLines(System.IO.Path.Combine(home, ExtraFile), dlls.ToArray()); }
            catch (Exception) { }
            foreach (string dll in dlls)
                if (!SkippedDlls.Contains(dll)) SkippedDlls.Add(dll);
        }

        /// Скинам, поставленным раньше, раздаёт значки (и записки о DLL) из архивов в Skins\_installed.
        /// Каждый архив смотрим один раз: просмотренные записаны в _installed\_previews.txt.
        internal static void BackfillPreviews()
        {
            try
            {
                if (SkinsDir.Length == 0 || !Directory.Exists(DoneDir)) return;
                string doneFile = System.IO.Path.Combine(DoneDir, "_previews.txt");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(doneFile)) foreach (string line in File.ReadAllLines(doneFile)) seen.Add(line);
                bool changed = false;
                foreach (string zip in Directory.GetFiles(DoneDir, "*.zip"))
                {
                    string key = System.IO.Path.GetFileName(zip) + "|" + Stamp(zip);
                    if (!seen.Add(key)) continue;
                    changed = true;
                    ArchiveInfo info = Inspect(zip);
                    if (info.Error != null || info.Packs.Count != 1) continue;
                    ArchivePack pack = info.Packs[0];
                    var homes = new List<string> { HomeOf(pack) };
                    string legacy = pack.Kind == SkinKind.Stitch ? LegacyStitchRoot : LegacyCustomizerRoot;
                    if (legacy.Length > 0) homes.Add(System.IO.Path.Combine(legacy, pack.Name));
                    foreach (string home in homes)
                    {
                        if (!Directory.Exists(home)) continue;
                        ExtractIcon(info, home, false);
                        if (info.Dlls.Count > 0 && !File.Exists(System.IO.Path.Combine(home, ExtraFile)))
                            try { File.WriteAllLines(System.IO.Path.Combine(home, ExtraFile), info.Dlls.ToArray()); }
                            catch (Exception) { }
                    }
                }
                if (changed) File.WriteAllLines(doneFile, new List<string>(seen).ToArray());
            }
            catch (Exception) { }
        }

        // ---- мелочи ----

        static bool IsMarker(string s)
        {
            return s.Equals("Stitchwork", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Patchwork", StringComparison.OrdinalIgnoreCase);
        }

        static bool UnderStitch(string name, Dictionary<string, ArchivePack> roots)
        {
            foreach (KeyValuePair<string, ArchivePack> pair in roots)
                if (pair.Value.Kind == SkinKind.Stitch
                    && name.StartsWith(pair.Value.Prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool Belongs(string name, ArchivePack pack, Dictionary<string, ArchivePack> roots)
        {
            if (!name.StartsWith(pack.Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (pack.Kind == SkinKind.Stitch) return true;
            if (UnderStitch(name, roots)) return false;
            // Файл принадлежит самому глубокому Customizer-паку, под который подходит.
            foreach (KeyValuePair<string, ArchivePack> pair in roots)
            {
                ArchivePack archivePack = pair.Value;
                if (archivePack == pack || archivePack.Kind != SkinKind.Customizer) continue;
                if (archivePack.Prefix.Length > pack.Prefix.Length
                    && name.StartsWith(archivePack.Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            }
            // Для пака в корне архива берём только папки (значки и README из корня не нужны).
            if (pack.Prefix.Length == 0 && name.IndexOf('/') < 0) return false;
            return true;
        }

        /// "MorningRain-NightmareHornet-1.8.0.zip" -> "NightmareHornet".
        internal static string NameFromZip(string fileName)
        {
            string s = System.IO.Path.GetFileNameWithoutExtension(fileName);
            s = VersionTail.Replace(s, "");
            int dash = s.IndexOf('-');
            if (dash > 0 && dash < s.Length - 1) s = s.Substring(dash + 1); // убираем автора (формат Thunderstore)
            return s.Length > 0 ? s : "Skin";
        }

        static string CleanName(string s)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return s.Length > 0 ? s : "Skin";
        }
    }
}
