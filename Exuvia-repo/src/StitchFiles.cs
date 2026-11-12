// ============================================================================
//  StitchFiles.cs — файлы картинок скина и правила имён T2D.
//  Раньше было частью Stitch.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    /// Один PNG спрайта из пака.
    internal sealed class SpriteFile
    {
        internal string Path;
        internal string Name;      // имя спрайта без тега
        internal string Collection = "";
        internal string Atlas = "";
        internal char Anchor;      // 't','b','l','r','c'; '\0' = не указан (низ по центру)
        internal int PivotX = -1;  // @p<x>_<y>; -1 = не указана
        internal int PivotY = -1;
        internal long Len;         // размер файла и время изменения — из списка папки
        internal long Ticks;
        internal bool HasPivot { get { return PivotX >= 0 && PivotY >= 0; } }

        // Ширину и высоту картинки можно узнать только из самого файла. Чтобы не открывать тысячи
        // файлов при просмотре папок, узнаём их в тот момент, когда картинка действительно понадобилась
        // (и запоминаем на диске — см. PngSizes в Files.cs).
        int width, height;
        bool sized;
        internal int Width { get { Size(); return width; } }
        internal int Height { get { Size(); return height; } }
        /// false — файл не открывается или это не PNG.
        internal bool Readable { get { Size(); return width > 0 && height > 0; } }

        void Size()
        {
            if (sized) return;
            sized = true;
            if (!PngSizes.Get(Path, Len, Ticks, out width, out height)) width = height = 0;
        }
    }

    /// Одна картинка из Sprites\T2D: замена обычного спрайта Unity (меню, инвентарь, фоны, эффекты).
    internal sealed class T2DFile
    {
        internal string Path;
        internal string Key;       // "<имя атласа>/<имя спрайта>" или просто "<имя спрайта>"
        internal string Name;      // имя спрайта без тегов
        internal float Scale = 1f; // тег @s<число>
        internal bool Enforced;    // тег @e или @s: держать размер как в игре
        internal long Len;
        internal long Ticks;
    }

    /// Какие картинки T2D действуют сейчас (по всем включённым пакам и условиям).
    internal sealed class T2DWanted
    {
        internal readonly Dictionary<string, T2DFile> ByKey =
            new Dictionary<string, T2DFile>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> NameToKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// Папки паков внутри Sprites\T2D: имена атласов и текстур, для которых есть хоть одна картинка.
        internal readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal int Count { get { return ByKey.Count; } }

        internal void Add(T2DFile file)
        {
            ByKey[file.Key] = file;
            int slash = file.Key.IndexOf('/');
            if (slash > 0) Folders.Add(file.Key.Substring(0, slash));
            string known;
            if (NameToKey.TryGetValue(file.Name, out known))
            {
                // Одно имя спрайта в двух разных атласах: по одному имени уже не найти.
                if (!string.Equals(known, file.Key, StringComparison.OrdinalIgnoreCase) && known.IndexOf('/') >= 0
                    && file.Key.IndexOf('/') >= 0)
                    Ambiguous.Add(file.Name);
            }
            else NameToKey[file.Name] = file.Key;
        }

        /// Ищет замену для спрайта игры: textureName — имя его текстуры, spriteName — имя спрайта.
        /// Порядок такой же, как у Stitchwork.
        internal T2DFile Find(string textureName, string spriteName)
        {
            if (ByKey.Count == 0) return null;
            bool atlas = T2DNames.IsAtlas(textureName);
            T2DFile file;
            if (atlas && ByKey.TryGetValue(T2DNames.Clean(textureName) + "/" + spriteName, out file)) return file;
            if (string.IsNullOrEmpty(spriteName)) return null;
            string key;
            if (!atlas && !Ambiguous.Contains(spriteName) && NameToKey.TryGetValue(spriteName, out key)
                && ByKey.TryGetValue(key, out file)) return file;
            return ByKey.TryGetValue(spriteName, out file) ? file : null;
        }

        /// Есть ли в паках картинки для этой текстуры игры (для какого-нибудь из её спрайтов).
        /// Нужно, чтобы понять, за какими анимированными объектами стоит следить каждый кадр:
        /// анимация обычно листает кадры одной и той же текстуры.
        internal bool Touches(string textureName)
        {
            if (Folders.Count == 0 || string.IsNullOrEmpty(textureName)) return false;
            return Folders.Contains(T2DNames.IsAtlas(textureName) ? T2DNames.Clean(textureName) : textureName);
        }
    }

    /// Имена текстур Unity: атлас спрайтов называется "sactx-0-2048x2048-BC7-Menu-76dc7263" -> "Menu".
    internal static class T2DNames
    {
        internal static bool IsAtlas(string textureName)
        {
            return textureName != null && (textureName.Contains("-BC7-") || textureName.Contains("DXT5|BC3-"));
        }

        internal static string Clean(string textureName)
        {
            if (textureName == null) return "";
            string marker = null;
            if (textureName.Contains("-BC7-")) marker = "-BC7-";
            else if (textureName.Contains("DXT5|BC3-")) marker = "DXT5|BC3-";
            else if (textureName.Contains("DXT5_BC3-")) marker = "DXT5_BC3-";
            if (marker == null) return textureName;
            string tail = textureName.Substring(textureName.IndexOf(marker, StringComparison.Ordinal) + marker.Length);
            int dash = tail.LastIndexOf('-');
            return dash > 0 ? tail.Substring(0, dash) : tail;
        }

        /// "name@e" -> держать размер игры; "name@s2" -> вдвое крупнее. Возвращает имя без тега.
        internal static string ParseSizeTags(string stem, out float scale, out bool enforced)
        {
            scale = 1f;
            enforced = false;
            if (string.IsNullOrEmpty(stem)) return stem;
            if (stem.Length > 2 && stem.EndsWith("@e", StringComparison.OrdinalIgnoreCase))
            {
                enforced = true;
                return stem.Substring(0, stem.Length - 2);
            }
            int at = stem.LastIndexOf("@s", StringComparison.OrdinalIgnoreCase);
            if (at <= 0 || at + 2 >= stem.Length) return stem;
            float value;
            if (!float.TryParse(stem.Substring(at + 2), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) || value <= 0f)
                return stem;
            scale = Math.Max(0.1f, Math.Min(10f, value));
            enforced = true;
            return stem.Substring(0, at);
        }
    }
}
