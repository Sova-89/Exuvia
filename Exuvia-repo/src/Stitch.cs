// ============================================================================
//  Stitch.cs — паки формата Stitchwork / Patchwork (без Unity, чтобы проверять отдельно).
//
//  Что это за формат (по описанию Patchwork и файлам реальных паков):
//    <папка пака>\Stitchwork\
//        pack.json                    — name, author, description
//        conditions.json              — именованные условия и список наборов (sets)
//        Sprites\<коллекция>\<атлас>\<имя спрайта>[@тег].png
//        Sprites\T2D\...              — отдельные Texture2D (меню, фоны), пока не применяются
//        Sets\<набор>\Sprites\...     — то же, но работает только когда условие набора верно
//        Sounds, Text, Recolor, ...   — звуки, тексты, перекраска (этим модом не применяются)
//    Теги в имени файла: @t @b @l @r @c — якорь увеличенной картинки (по умолчанию низ по центру),
//    @p<x>_<y> — точка опоры в пикселях картинки.
//    Где лежат паки: BepInEx\plugins\<что угодно>\Stitchwork и <папка игры>\Packs\<имя>\Stitchwork.
//
//  Включён ли пак — хранится в BepInEx\config\sova.exuvia.stitch.txt (по имени на строку).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    internal enum CondKind { Crest, Scene, SceneContains, PackActive }

    internal static class StitchStore
    {
        internal static string GameRoot = "";
        internal static string PluginsRoot = "";
        /// Папка Skins: скины лежат как Skins\<имя>\Stitchwork.
        internal static string SkinsRoot = "";
        internal static string StateFile = "";
        internal static Action<string> LogSink = delegate { };
        internal static readonly List<StitchPack> Packs = new List<StitchPack>();
        internal static int Version;

        static HashSet<string> enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool stateLoaded;

        internal static string Key(string collection, string atlas, string sprite)
        {
            return (collection + "|" + atlas + "|" + sprite).ToLowerInvariant();
        }

        internal static int EnabledCount
        {
            get
            {
                int n = 0;
                foreach (StitchPack pack in Packs) if (pack.Enabled) n++;
                return n;
            }
        }

        internal static List<StitchPack> EnabledPacks()
        {
            var list = new List<StitchPack>();
            foreach (StitchPack pack in Packs) if (pack.Enabled) list.Add(pack);
            return list;
        }

        // ---- поиск паков ----

        internal static void Scan()
        {
            LoadState();
            var found = new List<StitchPack>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (GameRoot.Length > 0)
                {
                    string packsDir = Path.Combine(GameRoot, "Packs");
                    if (Directory.Exists(packsDir))
                        foreach (string d in SafeDirs(packsDir))
                            foreach (string marker in new[] { "Stitchwork", "Patchwork" })
                                TryAdd(Path.Combine(d, marker), found, seen);
                }
                if (PluginsRoot.Length > 0 && Directory.Exists(PluginsRoot))
                    FindMarkers(PluginsRoot, 0, found, seen);
                if (SkinsRoot.Length > 0 && Directory.Exists(SkinsRoot))
                    foreach (string d in SafeDirs(SkinsRoot))
                    {
                        if (System.IO.Path.GetFileName(d).StartsWith("_")) continue; // _installed, _tmp
                        foreach (string marker in new[] { "Stitchwork", "Patchwork" })
                            TryAdd(Path.Combine(d, marker), found, seen);
                    }
            }
            catch (Exception e)
            {
                LogSink("не удалось просмотреть папки Stitchwork-паков: " + e.Message);
            }

            found.Sort(delegate (StitchPack a, StitchPack b) { return string.Compare(a.Name, b.Name,
                StringComparison.OrdinalIgnoreCase); });

            // Одинаковые имена различаем номером.
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (StitchPack pack in found)
            {
                int n;
                names.TryGetValue(pack.Name, out n);
                names[pack.Name] = n + 1;
                if (n > 0) pack.Name = pack.Name + " (" + (n + 1) + ")";
                pack.Enabled = enabled.Contains(pack.Name);
            }

            Packs.Clear();
            Packs.AddRange(found);
            Version++;
        }

        static void FindMarkers(string dir, int depth, List<StitchPack> found, HashSet<string> seen)
        {
            if (depth > 4) return;
            foreach (string d in SafeDirs(dir))
            {
                string name = System.IO.Path.GetFileName(d);
                if (string.Equals(name, "Exuvia", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Wardrobe", StringComparison.OrdinalIgnoreCase)) continue; // папка самого мода (и прежнее имя)
                if (string.Equals(name, "Stitchwork", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Patchwork", StringComparison.OrdinalIgnoreCase))
                {
                    TryAdd(d, found, seen);
                    continue; // внутри пака больше паков нет
                }
                FindMarkers(d, depth + 1, found, seen);
            }
        }

        static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch (Exception) { return new string[0]; }
        }

        static void TryAdd(string dir, List<StitchPack> found, HashSet<string> seen)
        {
            if (!Directory.Exists(dir)) return;
            string full = System.IO.Path.GetFullPath(dir);
            if (!seen.Add(full)) return;
            try
            {
                StitchPack pack = Load(full);
                if (pack != null) found.Add(pack);
            }
            catch (Exception e)
            {
                LogSink("пак " + full + " не прочитан: " + e.Message);
            }
        }

        // ---- чтение одного пака ----

        internal static StitchPack Load(string dir)
        {
            bool hasSprites = Directory.Exists(System.IO.Path.Combine(dir, "Sprites"))
                || Directory.Exists(System.IO.Path.Combine(dir, "Sets"));
            if (!hasSprites) return null; // например, у пака только звуки или тексты: нам нечего подменять

            var pack = new StitchPack();
            pack.Dir = dir;
            string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir));
            pack.Name = parent != null && parent.Length > 0 ? parent : System.IO.Path.GetFileName(dir);

            JObject meta = ReadJson(System.IO.Path.Combine(dir, "pack.json"), pack);
            if (meta != null)
            {
                string n = Str(meta, "name");
                if (n.Length > 0) pack.Name = n;
                pack.Author = Str(meta, "author");
                pack.Description = Str(meta, "description");
            }

            // Условия и порядок наборов.
            var listed = new List<KeyValuePair<string, JToken>>(); // имя набора, когда
            JObject cj = ReadJson(System.IO.Path.Combine(dir, "conditions.json"), pack);
            if (cj != null)
            {
                JObject named = cj["conditions"] as JObject;
                if (named != null)
                    foreach (JProperty prop in named.Properties()) pack.Named[prop.Name] = Cond.Parse(prop.Value);

                JArray sets = cj["sets"] as JArray;
                if (sets != null)
                {
                    foreach (JToken token in sets)
                    {
                        JObject so = token as JObject;
                        string sname = so != null ? Str(so, "name") : (token.Type == JTokenType.String ? (string)token
                            : "");
                        if (sname.Length == 0) continue;
                        listed.Add(new KeyValuePair<string, JToken>(sname, so != null ? so["when"] : null));
                    }
                }
            }

            // Слои: сначала перечисленные наборы, потом остальные папки Sets\ (по алфавиту), основной слой внизу.
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string setsRoot = System.IO.Path.Combine(dir, "Sets");
            foreach (KeyValuePair<string, JToken> pair in listed)
            {
                if (!done.Add(pair.Key)) continue;
                StitchLayer layer = NewLayer(pair.Key, System.IO.Path.Combine(setsRoot, pair.Key));
                if (pair.Value == null)
                {
                    // Без своего условия: набор включается условием с именем набора, если оно есть; иначе работает
                    // всегда.
                    if (pack.Named.ContainsKey(pair.Key)) { layer.When = new CondNamed { Name = pair.Key }; layer.WhenText = pair.Key; }
                }
                else
                {
                    layer.When = Cond.Parse(pair.Value);
                    layer.WhenText = pair.Value.Type == JTokenType.String ? (string)pair.Value : "условие";
                }
                pack.Layers.Add(layer);
            }
            if (Directory.Exists(setsRoot))
            {
                var rest = new List<string>(SafeDirs(setsRoot));
                rest.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string d in rest)
                {
                    string sname = System.IO.Path.GetFileName(d);
                    if (!done.Add(sname)) continue;
                    StitchLayer layer = NewLayer(sname, d);
                    layer.When = new CondNamed { Name = sname }; // имя папки и есть условие
                    layer.WhenText = sname;
                    pack.Layers.Add(layer);
                }
            }
            pack.Layers.Add(NewLayer("", dir)); // основной слой

            for (int i = 0; i < pack.Layers.Count; i++)
            {
                StitchLayer layer = pack.Layers[i];
                layer.Index = i;
                ScanLayer(layer, pack);
                foreach (string c in layer.Collections) pack.Collections.Add(c);
                pack.SpriteCount += layer.Sprites.Count;
                pack.T2DFiles += layer.T2DFiles;
            }
            CountExtras(pack, dir);
            if (pack.SpriteCount == 0 && pack.T2DFiles == 0) return null;
            return pack;
        }

        static StitchLayer NewLayer(string name, string dir)
        {
            var layer = new StitchLayer();
            layer.Name = name;
            layer.Dir = dir;
            return layer;
        }

        static void ScanLayer(StitchLayer layer, StitchPack pack)
        {
            string sprites = System.IO.Path.Combine(layer.Dir, "Sprites");
            if (!Directory.Exists(sprites)) return;

            foreach (string cdir in SafeDirs(sprites))
            {
                string collection = System.IO.Path.GetFileName(cdir);
                if (string.Equals(collection, "T2D", StringComparison.OrdinalIgnoreCase))
                {
                    ScanT2D(cdir, layer, pack);
                    continue;
                }
                foreach (string adir in SafeDirs(cdir))
                {
                    string atlas = System.IO.Path.GetFileName(adir);
                    // Один запрос к диску на папку: имена сразу с размером и временем изменения.
                    foreach (FileRec rec in FileLister.List(adir, ".png"))
                    {
                        SpriteFile file = NewSprite(rec.Path, rec.Len, rec.Ticks);
                        file.Collection = collection;
                        file.Atlas = atlas;
                        layer.Sprites[Key(collection, atlas, file.Name)] = file;
                        layer.Collections.Add(collection);
                        pack.Bytes += file.Len;
                    }
                }
            }
        }

        /// Sprites\T2D: файлы в корне и в папке _standalone ищутся по имени спрайта,
        /// файлы в остальных папках — по "<папка = имя атласа>/<имя спрайта>". Глубже одного уровня не смотрим (как
        /// Stitchwork).
        static void ScanT2D(string root, StitchLayer layer, StitchPack pack)
        {
            var dirs = new List<string>(SafeDirs(root));
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string folder = System.IO.Path.GetFileName(d);
                bool standalone = string.Equals(folder, "_standalone", StringComparison.OrdinalIgnoreCase);
                AddT2DFiles(d, standalone ? null : folder, layer, pack);
            }
            AddT2DFiles(root, null, layer, pack);
        }

        static void AddT2DFiles(string dir, string atlas, StitchLayer layer, StitchPack pack)
        {
            List<FileRec> files = FileLister.List(dir, ".png");
            files.Sort(delegate (FileRec a, FileRec b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Path,
                b.Path); });
            foreach (FileRec rec in files)
            {
                var file = new T2DFile();
                file.Path = rec.Path;
                file.Name = T2DNames.ParseSizeTags(System.IO.Path.GetFileNameWithoutExtension(rec.Path), out file.Scale,
                    out file.Enforced);
                if (string.IsNullOrEmpty(file.Name)) continue;
                file.Key = atlas != null ? atlas + "/" + file.Name : file.Name;
                file.Len = rec.Len;
                file.Ticks = rec.Ticks;
                if (layer.T2D.ContainsKey(file.Key)) continue; // _standalone важнее корня, первый найденный остаётся
                layer.T2D[file.Key] = file;
                layer.T2DFiles++;
                pack.Bytes += file.Len;
            }
        }

        static int CountFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Length; }
            catch (Exception) { return 0; }
        }

        static void CountExtras(StitchPack pack, string dir)
        {
            foreach (StitchLayer layer in pack.Layers)
            {
                string snd = System.IO.Path.Combine(layer.Dir, "Sounds");
                if (Directory.Exists(snd))
                {
                    string[] files;
                    try { files = Directory.GetFiles(snd, "*.*", SearchOption.AllDirectories); }
                    catch (Exception) { files = new string[0]; }
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".wav" && ext != ".ogg" && ext != ".mp3" && ext != ".aiff" && ext != ".aif")
                            continue;
                        string name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (!layer.Sounds.ContainsKey(name)) { layer.Sounds[name] = f; pack.SoundFiles++; }
                    }
                }
                if (Directory.Exists(System.IO.Path.Combine(layer.Dir, "Recolor"))
                    || Directory.Exists(System.IO.Path.Combine(layer.Dir, "Recolour"))) pack.HasRecolor = true;
                if (Directory.Exists(System.IO.Path.Combine(layer.Dir, "Text"))) pack.HasText = true;
            }
            if (Directory.Exists(System.IO.Path.Combine(dir, "Text"))) pack.HasText = true;
        }

        /// Запись о картинке спрайта: имя и теги — из имени файла, размер файла и время — из списка папки.
        /// Сам файл здесь не открывается (ширину и высоту SpriteFile узнаёт, когда они понадобятся).
        internal static SpriteFile NewSprite(string path, long len, long ticks)
        {
            var file = new SpriteFile();
            file.Path = path;
            file.Len = len;
            file.Ticks = ticks;
            ParseName(System.IO.Path.GetFileNameWithoutExtension(path), file);
            return file;
        }

        /// То же для одного файла, когда списка папки нет. null — файла нет или это не PNG.
        internal static SpriteFile ReadSprite(string path)
        {
            long len, ticks;
            if (!FileLister.Stat(path, out len, out ticks)) return null;
            SpriteFile file = NewSprite(path, len, ticks);
            return file.Readable ? file : null;
        }

        /// "Hornet_fall0002@p63_108" -> имя "Hornet_fall0002", точка опоры (63,108).
        internal static void ParseName(string stem, SpriteFile file)
        {
            file.Name = stem;
            file.Anchor = '\0';
            file.PivotX = file.PivotY = -1;

            int at = stem.LastIndexOf('@');
            if (at <= 0 || at == stem.Length - 1) return;
            string tag = stem.Substring(at + 1);

            if (tag.Length == 1 && "tblrc".IndexOf(char.ToLowerInvariant(tag[0])) >= 0)
            {
                file.Name = stem.Substring(0, at);
                file.Anchor = char.ToLowerInvariant(tag[0]);
                return;
            }
            if (tag.Length > 3 && (tag[0] == 'p' || tag[0] == 'P'))
            {
                int us = tag.IndexOf('_');
                int x, y;
                if (us > 1 && int.TryParse(tag.Substring(1, us - 1), out x)
                    && int.TryParse(tag.Substring(us + 1), out y) && x >= 0 && y >= 0)
                {
                    file.Name = stem.Substring(0, at);
                    file.PivotX = x;
                    file.PivotY = y;
                }
            }
            // Любой другой хвост остаётся частью имени (так делает и Stitchwork).
        }

        static JObject ReadJson(string path, StitchPack pack)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string text = File.ReadAllText(path);
                if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                return JObject.Parse(text);
            }
            catch (Exception e)
            {
                pack.Problems.Add(System.IO.Path.GetFileName(path) + ": " + e.Message);
                return null;
            }
        }

        static string Str(JObject json, string key)
        {
            JToken token = json[key];
            return token != null && token.Type == JTokenType.String ? (string)token : "";
        }

        // ---- включение и выключение ----

        static void LoadState()
        {
            if (stateLoaded) return;
            stateLoaded = true;
            enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (StateFile.Length > 0 && File.Exists(StateFile))
                    foreach (string line in File.ReadAllLines(StateFile))
                    {
                        string s = line.Trim();
                        if (s.Length > 0) enabled.Add(s);
                    }
            }
            catch (Exception e)
            {
                LogSink("не прочитан список включённых Stitchwork-паков: " + e.Message);
            }
        }

        static string SaveState()
        {
            try
            {
                if (StateFile.Length == 0) return null;
                string dir = System.IO.Path.GetDirectoryName(StateFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var names = new List<string>(enabled);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(StateFile, names.ToArray());
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// Включает или выключает пак. Возвращает текст ошибки или null.
        internal static string SetEnabled(StitchPack pack, bool on)
        {
            LoadState();
            if (on) enabled.Add(pack.Name); else enabled.Remove(pack.Name);
            pack.Enabled = on;
            Version++;
            return SaveState();
        }

        internal static string DisableAll()
        {
            LoadState();
            enabled.Clear();
            foreach (StitchPack pack in Packs) pack.Enabled = false;
            Version++;
            return SaveState();
        }

        // ---- что сейчас должно быть подменено ----

        /// Для каждой коллекции — какие файлы спрайтов действуют при текущих условиях.
        /// Паки накладываются по алфавиту, побеждает последний (как у Customizer-паков).
        /// State — короткая подпись: если она не изменилась, пересчитывать нечего.
        internal static string State(CondCtx ctx)
        {
            var builder = new StringBuilder();
            builder.Append(Version).Append(';');
            foreach (StitchPack pack in Packs)
            {
                if (!pack.Enabled) continue;
                ctx.Named = pack.Named;
                List<StitchLayer> active;
                builder.Append(pack.Name).Append(':').Append(pack.ActiveSignature(ctx, out active)).Append(';');
            }
            return builder.ToString();
        }

        internal static Dictionary<string, Wanted> BuildWanted(CondCtx ctx)
        {
            var map = new Dictionary<string, Wanted>(StringComparer.OrdinalIgnoreCase);
            foreach (StitchPack pack in Packs)
            {
                if (!pack.Enabled) continue;
                ctx.Named = pack.Named;
                List<StitchLayer> active;
                pack.ActiveSignature(ctx, out active);
                foreach (KeyValuePair<string, SpriteFile> pair in pack.Effective(active))
                {
                    SpriteFile file = pair.Value;
                    Wanted wanted;
                    if (!map.TryGetValue(file.Collection, out wanted))
                    {
                        wanted = new Wanted();
                        wanted.Collection = file.Collection;
                        map[file.Collection] = wanted;
                    }
                    wanted.Files[(file.Atlas + "|" + file.Name).ToLowerInvariant()] = file;
                }
            }
            foreach (Wanted wanted in map.Values) wanted.Seal();
            return map;
        }

        /// Картинки T2D, которые действуют при текущих условиях.
        internal static T2DWanted BuildWantedT2D(CondCtx ctx)
        {
            var wanted = new T2DWanted();
            foreach (StitchLayer layer in ActiveLayersBottomUp(ctx))
                foreach (T2DFile file in layer.T2D.Values) wanted.Add(file);
            return wanted;
        }

        /// Звуки, которые действуют при текущих условиях: имя звука -> файл.
        internal static Dictionary<string, string> BuildWantedSounds(CondCtx ctx)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (StitchLayer layer in ActiveLayersBottomUp(ctx))
                foreach (KeyValuePair<string, string> pair in layer.Sounds) map[pair.Key] = pair.Value;
            return map;
        }

        /// Правила перекраски для сцены из всех действующих слоёв.
        internal static SceneColors BuildSceneColors(CondCtx ctx, string scene)
        {
            var colors = new SceneColors();
            if (string.IsNullOrEmpty(scene)) return colors;
            foreach (StitchLayer layer in ActiveLayersBottomUp(ctx))
                foreach (string folder in new[] { "Recolor", "Recolour" })
                    StitchRecolor.LoadFile(System.IO.Path.Combine(System.IO.Path.Combine(layer.Dir, folder), scene
                        + ".txt"), colors);
            return colors;
        }

        /// Папки Text действующих слоёв (и самих паков) в порядке «кто позже, тот важнее».
        internal static List<string> TextDirs(CondCtx ctx)
        {
            var list = new List<string>();
            foreach (StitchLayer layer in ActiveLayersBottomUp(ctx))
            {
                string d = System.IO.Path.Combine(layer.Dir, "Text");
                if (Directory.Exists(d)) list.Add(d);
            }
            return list;
        }

        /// Действующие слои всех включённых паков в порядке «кто позже, тот важнее»:
        /// паки по алфавиту, внутри пака от основного слоя к верхнему набору.
        internal static List<StitchLayer> ActiveLayersBottomUp(CondCtx ctx)
        {
            var list = new List<StitchLayer>();
            foreach (StitchPack pack in Packs)
            {
                if (!pack.Enabled) continue;
                ctx.Named = pack.Named;
                List<StitchLayer> active;
                pack.ActiveSignature(ctx, out active);
                for (int i = active.Count - 1; i >= 0; i--) list.Add(active[i]);
            }
            return list;
        }

        /// Пересекаются ли пак и набор коллекций (скин меняет те же спрайты).
        internal static bool Shares(StitchPack pack, IEnumerable<string> collections)
        {
            foreach (string c in collections) if (pack.Collections.Contains(c)) return true;
            return false;
        }
    }
}
