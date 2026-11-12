// ============================================================================
//  Install.cs — установка скинов из папки Skins (без Unity, проверяется отдельно).
//
//  В <папка игры>\Skins можно кинуть что угодно: zip, rar, 7z или распакованную папку,
//  скин формата Customizer или Stitchwork/Patchwork. Мод сам разбирает это при запуске
//  игры и при открытии окна. Куда ставить, задают CustomizerRoot и StitchRoot; с 0.9.2 это сама
//  папка Skins — скины в ней и живут, по папке на скин:
//   • Customizer (папки с atlasN.png и/или Texture2D) -> Skins\<имя>
//     Файл active.txt не переносится: новый скин ставится выключенным.
//   • Stitchwork / Patchwork (папка Stitchwork или Patchwork) -> Skins\<имя>\Stitchwork
//   • Папка, которая уже лежит как надо (Skins\<имя>\<коллекция>\atlasN.png, Skins\<имя>\Stitchwork),
//     установки не требует и не трогается.
//   • Разобранный архив (и остатки папки: значки, readme) переезжают в Skins\_installed.
//  DLL из архивов не ставим: только картинки и данные скина.
//  rar и 7z распаковывает tar.exe из Windows; если он не справился — нужен zip или папка.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Exuvia
{
    internal enum SkinKind { Unknown, Customizer, Stitch }

    /// Один скин внутри архива.
    internal sealed class ArchivePack
    {
        internal SkinKind Kind;
        internal string Name;        // имя папки, в которую он встанет
        internal string Prefix;      // путь внутри архива (с '/' на конце или пустой), откуда брать файлы
        internal string Marker = ""; // для Stitch: "Stitchwork" или "Patchwork"
        internal string Target;      // куда распаковывать
        internal int Files;
        internal long Bytes;
        internal bool Installed;     // папка назначения уже есть
    }

    internal sealed class ArchiveInfo
    {
        internal string Path;
        internal string FileName;
        internal long Len;
        internal string Error;       // не читается или не установилось
        internal bool IsFolder;      // это папка, а не архив
        // уже пробовали установить и не вышло (сам больше не пробуем, пока файл не изменится)
        internal bool Failed;
        internal bool HasDll;        // в архиве есть свои DLL (мы их не ставим)
        internal string Icon;        // значок в корне архива (icon.png / preview.png) — пойдёт скину как превью
        internal readonly List<string> Dlls = new List<string>(); // имена этих DLL
        internal readonly List<ArchivePack> Packs = new List<ArchivePack>();
        internal bool AllInstalled
        {
            get
            {
                if (Packs.Count == 0) return false;
                foreach (ArchivePack pack in Packs) if (!pack.Installed) return false;
                return true;
            }
        }
    }

    internal static partial class Installer
    {
        internal static string SkinsDir = "";      // куда класть архивы
        internal static string CustomizerRoot = "";
        internal static string StitchRoot = "";    // <папка игры>\Packs
        internal static readonly List<ArchiveInfo> Archives = new List<ArchiveInfo>();

        /// Имя картинки-превью в папке скина и записки о DLL автора, которые мы не поставили.
        internal const string PreviewFile = "preview.png";
        internal const string ExtraFile = "exuvia-extra.txt";
        /// Старые места скинов (Mods\Customizer и Packs): туда тоже раскладываем значки из уже разобранных архивов.
        internal static string LegacyCustomizerRoot = "", LegacyStitchRoot = "";
        /// DLL авторов, замеченные при последней установке (для сообщения игроку).
        internal static readonly List<string> SkippedDlls = new List<string>();

        /// Выбор языка для сообщений об ошибках (ru, en). Задаёт Plugin; без него — по-русски.
        internal static Func<string, string, string> Say = delegate (string ru, string en) { return ru; };

        static readonly Regex AtlasPng = new Regex(@"^atlas\d+\.png$", RegexOptions.IgnoreCase);
        static readonly Regex VersionTail = new Regex(@"[-_ ]v?\d+(\.\d+)+$", RegexOptions.IgnoreCase);

        internal static void EnsureDir()
        {
            try
            {
                if (SkinsDir.Length == 0) return;
                Directory.CreateDirectory(SkinsDir);
                string note = System.IO.Path.Combine(SkinsDir, "README.txt");
                string text =
                        "Exuvia: кидай сюда скины для Silksong — zip, rar, 7z или распакованную папку.\r\n"
                        + "Подходят скины формата Customizer и Stitchwork/Patchwork.\r\n"
                        + "Мод сам разберёт их при запуске игры или когда откроешь экран «Скины».\r\n"
                        + "Скины остаются в этой папке: одна папка — один скин. Удалить скин — удалить его папку.\r\n"
                        + "Включать и выключать скины — на экране «Скины», прямо в игре.\r\n"
                        + "Разобранные архивы переезжают в папку _installed: она не нужна для работы, её можно "
                            + "чистить.\r\n\r\n"
                        + "Exuvia: drop Silksong skins here — zip, rar, 7z or an unpacked folder.\r\n"
                        + "Customizer and Stitchwork/Patchwork skins are supported.\r\n"
                        + "They are unpacked when the game starts or when you open the Skins screen.\r\n"
                        + "Skins stay in this folder: one folder per skin. To remove a skin, delete its folder.\r\n"
                        + "Turn skins on and off on the Skins screen, inside the game.\r\n"
                        + "Unpacked archives are moved to the _installed folder: it is not needed, feel free to clean "
                            + "it.\r\n";
                // Свою старую записку обновляем, чужой файл с таким именем не трогаем.
                string old = File.Exists(note) ? File.ReadAllText(note) : null;
                if (old == null || (old != text && (old.StartsWith("Exuvia:", StringComparison.Ordinal) || old.StartsWith("Wardrobe:", StringComparison.Ordinal))))
                    File.WriteAllText(note, text);
            }
            catch (Exception) { }
        }

        /// Чем распаковывать rar и 7z. Пусто = tar.exe из Windows.
        internal static string TarPath = "";

        static readonly Dictionary<string, string> failedStamp =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> failedWhy =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string DoneDir { get { return System.IO.Path.Combine(SkinsDir, "_installed"); } }

        /// Перечитывает, что лежит в папке Skins и ещё не установлено.
        internal static void Scan()
        {
            Archives.Clear();
            if (SkinsDir.Length == 0 || !Directory.Exists(SkinsDir)) return;
            var paths = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(SkinsDir))
                {
                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".zip" || ext == ".rar" || ext == ".7z") paths.Add(f);
                }
                foreach (string d in Directory.GetDirectories(SkinsDir))
                {
                    string folder = System.IO.Path.GetFileName(d);
                    if (folder.StartsWith("_")) continue; // _installed, _tmp — наши служебные
                    if (Settled(d)) continue;             // это уже стоящий скин
                    paths.Add(d);
                }
            }
            catch (Exception) { return; }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                ArchiveInfo info;
                if (Directory.Exists(path))
                {
                    info = new ArchiveInfo();
                    info.Path = path;
                    info.FileName = System.IO.Path.GetFileName(path);
                    info.IsFolder = true;
                }
                else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    info = Inspect(path);
                }
                else
                {
                    info = new ArchiveInfo();
                    info.Path = path;
                    info.FileName = System.IO.Path.GetFileName(path);
                    try { info.Len = new FileInfo(path).Length; } catch (Exception) { }
                }

                string stamp;
                if (failedStamp.TryGetValue(path, out stamp) && stamp == Stamp(path))
                {
                    info.Failed = true;
                    info.Error = failedWhy[path];
                }
                Archives.Add(info);
            }
        }

        /// Есть ли в Skins то, что ещё не пробовали установить.
        internal static bool HasNew
        {
            get
            {
                foreach (ArchiveInfo archive in Archives) if (!archive.Failed) return true;
                return false;
            }
        }

        /// Забыть прошлые неудачи: следующая установка попробует всё заново.
        internal static void ForgetFailures()
        {
            failedStamp.Clear();
            failedWhy.Clear();
            foreach (ArchiveInfo archive in Archives) archive.Failed = false;
        }

        /// Устанавливает всё новое из Skins. installed — имена установленных скинов, errors — что не вышло.
        internal static void ProcessAll(List<string> installed, List<string> errors)
        {
            SkippedDlls.Clear();
            foreach (ArchiveInfo archive in new List<ArchiveInfo>(Archives))
            {
                if (archive.Failed) continue;
                string stamp = Stamp(archive.Path);
                string error = Process(archive, installed);
                if (error == null) continue;
                archive.Failed = true;
                archive.Error = error;
                failedStamp[archive.Path] = stamp;
                failedWhy[archive.Path] = error;
                errors.Add(archive.FileName + ": " + error);
            }
        }

        static string Stamp(string path)
        {
            try
            {
                if (Directory.Exists(path)) return "d" + Directory.GetLastWriteTimeUtc(path).Ticks;
                var info = new FileInfo(path);
                return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception) { return "?"; }
        }

        static string Process(ArchiveInfo archive, List<string> installed)
        {
            try
            {
                if (archive.IsFolder)
                {
                    int n = InstallFolder(archive.Path, System.IO.Path.GetFileName(archive.Path), installed);
                    if (n == 0) return Say("в папке не найден скин (нет папок с atlasN.png, Texture2D или Stitchwork)",
                        "no skin found in the folder (no folders with atlasN.png, Texture2D or Stitchwork)");
                    if (Directory.Exists(archive.Path))
                    {
                        if (InPlace(archive.Path)) RemoveEmptyDirs(archive.Path); // скин встал в эту же папку
                        else MoveToDone(archive.Path);                      // остатки: значки, readme
                    }
                    return null;
                }

                if (archive.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (archive.Error != null) return archive.Error;
                    if (archive.Packs.Count == 0)
                        return Say("в архиве не найден скин (нет папок с atlasN.png, Texture2D или Stitchwork)",
                        "no skin found in the archive (no folders with atlasN.png, Texture2D or Stitchwork)");
                    int files;
                    string error = Install(archive, out files);
                    if (error != null) return error;
                    foreach (ArchivePack pack in archive.Packs) installed.Add(pack.Name);
                    MoveToDone(archive.Path);
                    return null;
                }

                // rar и 7z: распаковываем во временную папку и разбираем как папку.
                string stem = System.IO.Path.GetFileNameWithoutExtension(archive.Path);
                string tmpRoot = System.IO.Path.Combine(SkinsDir, "_tmp");
                string tmp = System.IO.Path.Combine(tmpRoot, CleanName(stem));
                DeleteTree(tmpRoot);
                Directory.CreateDirectory(tmp);
                string tarError = Untar(archive.Path, tmp);
                if (tarError != null)
                {
                    DeleteTree(tmpRoot);
                    return tarError;
                }
                int count = InstallFolder(tmp, stem, installed);
                DeleteTree(tmpRoot);
                if (count == 0) return Say("в архиве не найден скин (нет папок с atlasN.png, Texture2D или Stitchwork)",
                    "no skin found in the archive (no folders with atlasN.png, Texture2D or Stitchwork)");
                MoveToDone(archive.Path);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }
}
