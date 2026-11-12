// ============================================================================
//  Packs.cs — паки скинов на диске: поиск, включение и выключение.
//  Формат тот же, что у Silksong Customizer, старые паки работают как есть:
//    Hollow Knight Silksong_Data\Mods\Customizer\<пак>\<коллекция>\<текстура>.png
//    Hollow Knight Silksong_Data\Mods\Customizer\<пак>\Texture2D\*.png (это не наше)
//  Пак включён, если в его папке лежит файл active.txt (пустой). Включение и
//  выключение просто создают и удаляют этот файл, поэтому всё совместимо
//  с Customizer и CustomizerT2D.
//  Здесь нет типов Unity — файл проверяется отдельными тестами.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;

namespace Exuvia
{
    internal sealed class PackInfo
    {
        internal string Name;
        internal string Dir;
        internal bool Enabled;

        /// коллекция -> (имя текстуры без .png -> путь к файлу). Регистр не важен.
        internal Dictionary<string, Dictionary<string, string>> Files =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        internal int FileCount;
        internal long Bytes;
        internal int Texture2DCount;

        internal int CollectionCount { get { return Files.Count; } }

        internal List<string> CollectionNames()
        {
            var names = new List<string>(Files.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    internal static class PackStore
    {
        internal const string ActiveFile = "active.txt";
        internal const string Texture2DFolder = "Texture2D";

        /// Папки пака, в которых нет атласов спрайтов (их читают другие моды).
        static readonly string[] NotCollections = { "Texture2D", "VideoClip", "Video", "layered_images", "Stitchwork",
            "Patchwork" };

        /// Папки пака с видео-заставками и какие файлы в них считаются видео (как у CustomizerCinematic).
        static readonly string[] VideoFolders = { "Video", "VideoClip" };
        static readonly string[] VideoExtensions = { ".webm", ".mp4" };
        /// Имя файла может отличаться от имени заставки в игре этим хвостом.
        static readonly string[] VideoSuffixes = { "_30", "_60", "_30_fps", "_60_fps" };
        static Dictionary<string, string> videos;
        static int videosVersion = -1;

        /// Папка с паками. Задаётся в Plugin.Awake.
        internal static string Root = "";

        /// Папка Skins: скины в ней лежат по папке на скин вперемешку со скинами другого формата
        /// и ещё не разобранным, поэтому берём только папки, похожие на скин Customizer.
        internal static string ExtraRoot = "";

        static List<PackInfo> packs = new List<PackInfo>();
        static List<PackInfo> enabled = new List<PackInfo>();

        /// Растёт при каждом изменении списка или включённости; по нему движок видит, что пора применять.
        internal static int Version;

        internal static List<PackInfo> Packs { get { return packs; } }

        // ---- поиск паков -------------------------------------------------------

        internal static void Scan()
        {
            var found = new List<PackInfo>();
            try
            {
                if (Directory.Exists(Root))
                {
                    string[] dirs = Directory.GetDirectories(Root);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    foreach (string dir in dirs)
                    {
                        try { found.Add(ReadPack(dir)); }
                        catch (Exception e) { Log("пак «" + Path.GetFileName(dir) + "» пропущен: " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                Log("не удалось прочитать " + Root + ": " + e.Message);
            }
            try
            {
                if (ExtraRoot.Length > 0 && Directory.Exists(ExtraRoot))
                {
                    string[] dirs = Directory.GetDirectories(ExtraRoot);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    foreach (string dir in dirs)
                    {
                        if (Path.GetFileName(dir).StartsWith("_")) continue;
                        if (!Installer.IsCustomizerPack(dir)) continue;
                        try { found.Add(ReadPack(dir)); }
                        catch (Exception e) { Log("пак «" + Path.GetFileName(dir) + "» пропущен: " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                Log("не удалось прочитать " + ExtraRoot + ": " + e.Message);
            }

            // Порядок наложения — по имени, как в Customizer; при равных именах старая папка раньше.
            var order = new Dictionary<PackInfo, int>();
            for (int i = 0; i < found.Count; i++) order[found[i]] = i;
            found.Sort(delegate (PackInfo a, PackInfo b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : order[a].CompareTo(order[b]);
            });
            // Одинаковые имена (скин стоит и в старой папке, и в Skins) различаем номером.
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (PackInfo pack in found)
            {
                int n;
                names.TryGetValue(pack.Name, out n);
                names[pack.Name] = n + 1;
                if (n > 0) pack.Name = pack.Name + " (" + (n + 1) + ")";
            }
            packs = found;
            RebuildEnabled();
        }

        static PackInfo ReadPack(string dir)
        {
            var pack = new PackInfo();
            pack.Dir = dir;
            pack.Name = Path.GetFileName(dir);
            pack.Enabled = File.Exists(Path.Combine(dir, ActiveFile));

            foreach (string sub in Directory.GetDirectories(dir))
            {
                string folder = Path.GetFileName(sub);
                if (string.Equals(folder, Texture2DFolder, StringComparison.OrdinalIgnoreCase))
                {
                    pack.Texture2DCount = CountPng(sub);
                    continue;
                }
                if (IsNotCollection(folder)) continue;

                Dictionary<string, string> textures = null;
                // Один запрос к диску на папку: имена сразу с размером.
                foreach (FileRec rec in FileLister.List(sub, ".png"))
                {
                    if (textures == null) textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    textures[Path.GetFileNameWithoutExtension(rec.Path)] = rec.Path;
                    pack.FileCount++;
                    pack.Bytes += rec.Len;
                }
                if (textures != null) pack.Files[folder] = textures;
            }
            return pack;
        }

        static bool IsNotCollection(string folder)
        {
            foreach (string name in NotCollections)
                if (string.Equals(folder, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static int CountPng(string dir)
        {
            try { return Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories).Length; }
            catch (Exception) { return 0; }
        }

        static void RebuildEnabled()
        {
            var list = new List<PackInfo>();
            foreach (PackInfo pack in packs) if (pack.Enabled) list.Add(pack);
            enabled = list; // packs уже по алфавиту: порядок наложения = порядок Customizer
            Version++;
        }

        // ---- выбор файла для текстуры ------------------------------------------

        /// Имя текстуры игры или файла из папки Texture2D -> ключ, по которому они сопоставляются
        /// (правило взято у CustomizerT2D, чтобы подходили те же скины): «|» заменяется на «_»;
        /// у атласов («sactx-…») отбрасывается хвост «-<6–10 шестнадцатеричных знаков>».
        internal static string TextureKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            name = name.Replace("|", "_");
            if (name.StartsWith("sactx-", StringComparison.OrdinalIgnoreCase))
            {
                int dash = name.LastIndexOf('-');
                int tail = name.Length - (dash + 1);
                if (dash > 0 && dash < name.Length - 1 && tail >= 6 && tail <= 10)
                {
                    bool hex = true;
                    for (int i = dash + 1; i < name.Length; i++)
                    {
                        char c = name[i];
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A'
                            && c <= 'F'))) { hex = false; break; }
                    }
                    if (hex) return name.Substring(0, dash);
                }
            }
            return name;
        }

        /// Файл, которым заменить текстуру texture в коллекции collection, или null.
        /// Если пак не один, побеждает последний по алфавиту (как в Customizer).
        internal static string Resolve(string collection, string texture)
        {
            for (int i = enabled.Count - 1; i >= 0; i--)
            {
                Dictionary<string, string> textures;
                if (!enabled[i].Files.TryGetValue(collection, out textures)) continue;
                string path;
                if (textures.TryGetValue(texture, out path)) return path;
            }
            return null;
        }

        /// Видео-заставка из включённых паков для заставки игры с таким именем (null — нет).
        /// Ищется файл с тем же именем или с хвостом _30 / _60 / _30_fps / _60_fps в папках Video и
        /// VideoClip; если подходящий файл есть в нескольких паках — берётся из последнего по алфавиту.
        internal static string VideoFor(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            Dictionary<string, string> index = VideoIndex();
            if (index.Count == 0) return null;
            string path;
            if (index.TryGetValue(clipName, out path)) return path;
            foreach (string suffix in VideoSuffixes)
                if (index.TryGetValue(clipName + suffix, out path)) return path;
            return null;
        }

        /// Сколько видео-заставок во включённых паках.
        internal static int VideoCount { get { return VideoIndex().Count; } }

        static Dictionary<string, string> VideoIndex()
        {
            if (videos != null && videosVersion == Version) return videos;
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackInfo pack in enabled)
            {
                if (string.IsNullOrEmpty(pack.Dir)) continue;
                foreach (string folder in VideoFolders)
                {
                    string dir = Path.Combine(pack.Dir, folder);
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        string[] files = Directory.GetFiles(dir);
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (string file in files)
                        {
                            string ext = Path.GetExtension(file);
                            bool video = false;
                            foreach (string e in VideoExtensions)
                                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) video = true;
                            if (video) index[Path.GetFileNameWithoutExtension(file)] = Path.GetFullPath(file);
                        }
                    }
                    catch (Exception e)
                    {
                        if (LogSink != null) LogSink(dir + ": " + e.Message);
                    }
                }
            }
            videos = index;
            videosVersion = Version;
            return index;
        }

        /// Все файлы включённых паков (нужно, чтобы выбросить из памяти остальные).
        internal static HashSet<string> EnabledPaths()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackInfo pack in enabled)
                foreach (Dictionary<string, string> textures in pack.Files.Values)
                    foreach (string path in textures.Values) set.Add(path);
            return set;
        }

        internal static int EnabledCount { get { return enabled.Count; } }

        // ---- конфликты ---------------------------------------------------------

        /// Заменяют ли два пака хотя бы одну и ту же текстуру.
        internal static bool Conflicts(PackInfo a, PackInfo b)
        {
            if (a == b) return false;
            foreach (KeyValuePair<string, Dictionary<string, string>> pair in a.Files)
            {
                Dictionary<string, string> other;
                if (!b.Files.TryGetValue(pair.Key, out other)) continue;
                foreach (string texture in pair.Value.Keys)
                    if (other.ContainsKey(texture)) return true;
            }
            return false;
        }

        /// Какие включённые паки выключатся, если включить pack.
        internal static List<PackInfo> WouldDisable(PackInfo pack)
        {
            var list = new List<PackInfo>();
            foreach (PackInfo skinPack in packs)
                if (skinPack.Enabled && skinPack != pack && Conflicts(skinPack, pack)) list.Add(skinPack);
            return list;
        }

        // ---- включение и выключение --------------------------------------------

        /// Включает или выключает пак. При exclusive включение выключает пересекающиеся паки.
        /// Возвращает null, если всё хорошо, иначе текст ошибки.
        internal static string SetEnabled(PackInfo pack, bool on, bool exclusive, List<string> disabledNames)
        {
            try
            {
                if (on && exclusive)
                {
                    foreach (PackInfo other in WouldDisable(pack))
                    {
                        DeleteMarker(other);
                        other.Enabled = false;
                        if (disabledNames != null) disabledNames.Add(other.Name);
                    }
                }
                if (on) File.WriteAllText(Path.Combine(pack.Dir, ActiveFile), "");
                else DeleteMarker(pack);
                pack.Enabled = on;
            }
            catch (Exception e)
            {
                RebuildEnabled();
                return e.Message;
            }
            RebuildEnabled();
            return null;
        }

        /// Выключает все паки (чистая игра).
        internal static string DisableAll()
        {
            string error = null;
            foreach (PackInfo pack in packs)
            {
                if (!pack.Enabled) continue;
                try { DeleteMarker(pack); pack.Enabled = false; }
                catch (Exception e) { error = e.Message; }
            }
            RebuildEnabled();
            return error;
        }

        static void DeleteMarker(PackInfo pack)
        {
            string marker = Path.Combine(pack.Dir, ActiveFile);
            if (File.Exists(marker)) File.Delete(marker);
        }

        // ---- мелочи ------------------------------------------------------------

        /// Подключается из Plugin, чтобы писать в лог BepInEx. В тестах остаётся пустым.
        internal static Action<string> LogSink;

        static void Log(string text)
        {
            if (LogSink != null) LogSink(text);
        }
    }
}
