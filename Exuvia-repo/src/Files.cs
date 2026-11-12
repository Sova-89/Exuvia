// ============================================================================
//  Files.cs — быстрый просмотр папок со скинами (не зависит от Unity).
//
//  Зачем: в скинах тысячи мелких PNG. Если про каждый спрашивать диск отдельно
//  (а тем более открывать его ради размера картинки), запуск игры растягивается
//  на десятки секунд. Здесь два приёма:
//
//  1. FileLister — список файлов папки сразу с размером и временем изменения.
//     Windows отдаёт их вместе с именами одним запросом на папку, так что
//     отдельных обращений к каждому файлу нет.
//
//  2. PngSizes — ширина и высота PNG. Узнать их можно только из самого файла,
//     поэтому один раз прочитанное запоминаем в файле-памятке (BepInEx\cache\
//     Exuvia\png-sizes.txt). Запись годится, пока у файла те же размер в
//     байтах и время изменения; поменяли картинку — прочитаем заново.
//     Спрашивают размер только про те картинки, которые сейчас нужны игре
//     (включённый скин, коллекция на экране), а не про всё подряд.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;

namespace Exuvia
{
    /// Файл из списка папки.
    internal struct FileRec
    {
        internal string Path;
        internal long Len;
        internal long Ticks; // время изменения (UTC), как FileInfo.LastWriteTimeUtc.Ticks
    }

    internal static class FileLister
    {
        /// true — быстрый способ на этой системе недоступен, работаем обычным.
        static bool slowOnly;

        /// Сколько раз пришлось спрашивать диск про отдельный файл (для журнала).
        internal static int SingleStats;

        /// Файлы папки (без вложенных папок). ext — нужное расширение с точкой (".png") или null — все.
        /// Папки нет или она недоступна — пустой список.
        internal static List<FileRec> List(string dir, string ext)
        {
            if (!slowOnly)
            {
                try
                {
                    return Fast(dir, ext);
                }
                catch (IOException) { return new List<FileRec>(); }
                catch (UnauthorizedAccessException) { return new List<FileRec>(); }
                catch (Exception)
                {
                    // Нет нужных классов (другая сборка Mono) — дальше только обычным способом.
                    slowOnly = true;
                }
            }
            return Slow(dir, ext);
        }

        // Отдельный метод: если нужных классов нет, ошибка возникнет при входе сюда, и её поймает List.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<FileRec> Fast(string dir, string ext)
        {
            var options = new EnumerationOptions();
            options.RecurseSubdirectories = false;
            options.IgnoreInaccessible = true;
            options.AttributesToSkip = 0; // скрытые файлы тоже считаем, как Directory.GetFiles
            var all = new FileSystemEnumerable<FileRec>(dir, ToRec, options);
            all.ShouldIncludePredicate = IsFile;

            var result = new List<FileRec>();
            foreach (FileRec rec in all)
                if (ext == null || rec.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) result.Add(rec);
            return result;
        }

        static FileRec ToRec(ref FileSystemEntry entry)
        {
            FileRec rec;
            rec.Path = entry.ToFullPath();
            rec.Len = entry.Length;
            rec.Ticks = entry.LastWriteTimeUtc.UtcTicks;
            return rec;
        }

        static bool IsFile(ref FileSystemEntry entry)
        {
            return !entry.IsDirectory;
        }

        static List<FileRec> Slow(string dir, string ext)
        {
            var result = new List<FileRec>();
            string[] files;
            try { files = Directory.GetFiles(dir, ext != null ? "*" + ext : "*"); }
            catch (Exception) { return result; }
            foreach (string f in files)
            {
                // "*.png" цепляет и ".pngx"
                if (ext != null && !f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                FileRec rec;
                rec.Path = f;
                try
                {
                    var info = new FileInfo(f);
                    rec.Len = info.Length;
                    rec.Ticks = info.LastWriteTimeUtc.Ticks;
                    SingleStats++;
                }
                catch (Exception) { continue; }
                result.Add(rec);
            }
            return result;
        }

        /// То же для одного файла (когда списка папки под рукой нет).
        internal static bool Stat(string path, out long len, out long ticks)
        {
            len = 0;
            ticks = 0;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                len = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
                SingleStats++;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// Ширина и высота PNG с памяткой на диске.
    internal static class PngSizes
    {
        const int KeepDays = 90; // записи о картинках, которые давно не спрашивали, выбрасываем

        sealed class Entry
        {
            internal long Len, Ticks;
            internal int W, H;
            internal int Day; // когда спрашивали в последний раз (дни от 2020 года)
        }

        /// Где лежит памятка. Пусто — на диск не пишем (только память этого запуска).
        internal static string CacheFile = "";

        /// Сколько файлов пришлось открыть ради размера (для журнала).
        internal static int HeaderReads;

        static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static bool loaded, dirty;

        static int Today()
        {
            return (int)(DateTime.UtcNow - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
        }

        /// Размер картинки. len и ticks — размер файла и время изменения из списка папки.
        /// false — файл не читается или это не PNG.
        internal static bool Get(string path, long len, long ticks, out int w, out int h)
        {
            if (!loaded) Load();
            Entry entry;
            if (map.TryGetValue(path, out entry) && entry.Len == len && entry.Ticks == ticks)
            {
                int today = Today();
                if (entry.Day != today) { entry.Day = today; dirty = true; }
                w = entry.W;
                h = entry.H;
                return w > 0 && h > 0;
            }

            bool ok = ReadHeader(path, out w, out h);
            HeaderReads++;
            if (!ok) { w = 0; h = 0; }
            // Неудачу тоже запоминаем: битый файл незачем открывать при каждом запуске.
            map[path] = new Entry { Len = len, Ticks = ticks, W = w, H = h, Day = Today() };
            dirty = true;
            return ok;
        }

        /// Ширина и высота из заголовка PNG (первые 24 байта файла, без расшифровки картинки).
        internal static bool ReadHeader(string path, out int w, out int h)
        {
            w = 0;
            h = 0;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    var b = new byte[24];
                    if (stream.Read(b, 0, 24) < 24) return false;
                    if (b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47) return false; // подпись PNG
                    w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                    h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                }
            }
            catch (Exception)
            {
                return false;
            }
            return w > 0 && h > 0;
        }

        static void Load()
        {
            loaded = true;
            try
            {
                if (CacheFile.Length == 0 || !File.Exists(CacheFile)) return;
                int today = Today();
                foreach (string line in File.ReadAllLines(CacheFile, Encoding.UTF8))
                {
                    // размер, время, ширина, высота, день, путь (путь последним: в нём могут быть любые знаки)
                    string[] p = line.Split(new[] { '\t' }, 6);
                    if (p.Length != 6) continue;
                    var entry = new Entry();
                    if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Len))
                        continue;
                    if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Ticks))
                        continue;
                    if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.W)) continue;
                    if (!int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.H)) continue;
                    if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Day))
                        continue;
                    if (today - entry.Day > KeepDays) { dirty = true; continue; }
                    map[p[5]] = entry;
                }
            }
            catch (Exception)
            {
                // Памятка испорчена — начнём заново, это только вопрос скорости.
                map.Clear();
            }
        }

        /// Записывает памятку на диск, если в ней что-то изменилось. Возвращает ошибку или null.
        internal static string Save()
        {
            if (!dirty || CacheFile.Length == 0) return null;
            dirty = false;
            try
            {
                string dir = Path.GetDirectoryName(CacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var builder = new StringBuilder(map.Count * 96);
                foreach (KeyValuePair<string, Entry> pair in map)
                {
                    Entry entry = pair.Value;
                    builder.Append(entry.Len.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.W.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.H.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Day.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(pair.Key).Append('\n');
                }
                // Сначала во временный файл: если игру закроют посреди записи, старая памятка уцелеет.
                string tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, builder.ToString(), new UTF8Encoding(false));
                if (File.Exists(CacheFile)) File.Delete(CacheFile);
                File.Move(tmp, CacheFile);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        internal static bool Dirty { get { return dirty; } }
        internal static int Count { get { return map.Count; } }

        /// Для проверок: забыть всё, что помним в памяти.
        internal static void Forget()
        {
            map.Clear();
            loaded = false;
            dirty = false;
            HeaderReads = 0;
        }
    }
}
