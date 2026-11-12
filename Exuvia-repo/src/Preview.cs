// ============================================================================
//  Preview.cs — картинка-превью скина для экрана «Скины».
//
//  Откуда берётся (по порядку):
//   1) своя картинка скина: preview.png, icon.png, cover.png или thumbnail.png в папке
//      скина (значок из архива кладёт туда установщик, см. Install.cs);
//   2) готовое превью из прошлого запуска: BepInEx\cache\Exuvia\previews;
//   3) делается из картинок самого скина в фоновом потоке (Thumb.cs) и запоминается.
//  Пока превью нет или его не из чего сделать, Get возвращает null.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Exuvia
{
    internal static class Preview
    {
        const int Size = 320; // стоящая Хорнет с листа (около 230 x 270) помещается без уменьшения
        static readonly string[] OwnNames = { "preview.png", "icon.png", "cover.png", "thumbnail.png" };

        sealed class Item
        {
            internal Texture2D Tex;
            internal bool Busy;
            internal string CacheFile;
            internal byte[] Pixels;
            internal int W, H;
        }

        internal static string CacheDir = "";
        static readonly Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        static readonly object owner = new object();
        static int busy;

        /// Превью скина из папки dir (stitch — это папка Stitchwork скина). null — нет или ещё делается.
        internal static Texture2D Get(string dir, bool stitch)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            Item item;
            if (!items.TryGetValue(dir, out item))
            {
                item = new Item();
                items[dir] = item;
                try { Start(item, dir, stitch); }
                catch (Exception e) { Plugin.Log.LogWarning("Exuvia: превью скина не получилось (" + e.Message + ")."); }
            }
            return item.Tex;
        }

        /// Забыть всё (после установки скинов: у скина могла появиться своя картинка).
        internal static void Forget()
        {
            foreach (Item item in items.Values)
                if (item.Tex != null) UnityEngine.Object.Destroy(item.Tex);
            items.Clear();
        }

        internal static void Tick()
        {
            if (busy > 0) while (DecodeJobs.PumpOne()) { }
        }

        static void Start(Item item, string dir, bool stitch)
        {
            // 1) своя картинка скина
            var places = new List<string>();
            if (stitch) places.Add(Path.GetDirectoryName(dir.TrimEnd('\\', '/')));
            places.Add(dir);
            foreach (string place in places)
                foreach (string name in OwnNames)
                {
                    string own = Path.Combine(place, name);
                    if (File.Exists(own) && Load(item, own)) return;
                }

            // 2) сделанное раньше
            bool crop;
            string source = Thumb.PickSource(dir, stitch, out crop);
            if (source == null) return;
            var info = new FileInfo(source);
            // «v2» — превью стало одной Хорнет вместо куска листа: старые готовые картинки не годятся
            string stamp = "v2|" + source.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            item.CacheFile = CacheDir.Length > 0 ? Path.Combine(CacheDir, Hash(stamp) + ".png") : null;
            if (item.CacheFile != null && File.Exists(item.CacheFile) && Load(item, item.CacheFile)) return;

            // 3) делаем в фоне
            var job = new DecodeJob();
            job.Owner = owner;
            job.Tag = item;
            job.Path = source;
            job.Work = delegate (DecodeJob j)
            {
                string problem;
                item.Pixels = Thumb.Make(source, crop, Size, out item.W, out item.H, out problem, Thumb.IsHornetSheet(source));
                j.Problem = problem;
            };
            job.Done = Finish;
            item.Busy = true;
            busy++;
            DecodeJobs.Add(job);
        }

        static void Finish(DecodeJob job)
        {
            busy--;
            var item = (Item)job.Tag;
            item.Busy = false;
            if (item.Pixels == null) return;
            try
            {
                var tex = new Texture2D(item.W, item.H, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
                tex.name = "Exuvia Preview";
                tex.LoadRawTextureData(item.Pixels);
                tex.Apply(false, false);
                item.Tex = tex;
                if (item.CacheFile != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.CacheFile));
                    File.WriteAllBytes(item.CacheFile, ImageConversion.EncodeToPNG(tex));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: превью скина не получилось (" + e.Message + ").");
            }
            item.Pixels = null;
        }

        static bool Load(Item item, string path)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.name = "Exuvia Preview";
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path), true))
                {
                    UnityEngine.Object.Destroy(tex);
                    return false;
                }
                item.Tex = tex;
                return true;
            }
            catch (Exception) { return false; }
        }

        static string Hash(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in text) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }
    }
}
