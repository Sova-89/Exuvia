// ============================================================================
//  Engine.Cache.cs — часть класса Engine: уборка памяти и запас недавно выключенных скинов.
//  Общее описание — в начале Engine.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Exuvia
{
    internal static partial class Engine
    {
        // ---- уборка ------------------------------------------------------------

        /// Забываем материалы, которых уже нет (коллекцию выгрузила игра).
        static void PurgeDead()
        {
            List<int> dead = null;
            foreach (KeyValuePair<int, Rec> pair in recs)
            {
                if (pair.Value.Mat != null) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pair.Key);
            }
            if (dead != null) foreach (int id in dead) recs.Remove(id);
        }

        /// Уборка после прохода: помечаем, что лежит про запас, и следим за пределами памяти.
        static void Trim()
        {
            if (cache.Count == 0) return;
            RefreshIdle();
            Spare.Trim(); // общий предел запаса недавних скинов

            // Потолок памяти для включённых скинов: убираем то, что давно не нужно и сейчас ни на что не надето
            // (например, атласы боссов из давно покинутых мест).
            if (MaxCacheMb > 0)
            {
                var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Rec rec in recs.Values) if (rec.Path != null) inUse.Add(rec.Path);
                long limit = (long)MaxCacheMb * 1024 * 1024;
                long active = 0;
                var loose = new List<KeyValuePair<string, Cached>>();
                foreach (KeyValuePair<string, Cached> pair in cache)
                {
                    if (pair.Value.Tex == null || pair.Value.Idle) continue;
                    active += pair.Value.Bytes;
                    if (!inUse.Contains(pair.Key)) loose.Add(pair);
                }
                if (active > limit)
                {
                    loose.Sort(delegate (KeyValuePair<string, Cached> a,
                        KeyValuePair<string, Cached> b) { return a.Value.Used.CompareTo(b.Value.Used); });
                    int evicted = 0;
                    foreach (KeyValuePair<string, Cached> pair in loose)
                    {
                        if (active <= limit) break;
                        active -= pair.Value.Bytes;
                        Evict(pair.Key);
                        evicted++;
                    }
                    if (evicted > 0) Plugin.Log.LogInfo("Exuvia: текстуры включённых скинов превысили " + MaxCacheMb
                        + " МБ, убрано давно не нужных: " + evicted);
                }
            }
        }

        // ---- запас недавно выключенных скинов (для Spare.cs) -------------------------

        /// Помечает, какие текстуры лежат про запас (их пак выключен и они ни на что не надеты).
        internal static void RefreshIdle()
        {
            if (cache.Count == 0) return;
            HashSet<string> needed = PackStore.EnabledPaths();
            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Rec rec in recs.Values) if (rec.Path != null) inUse.Add(rec.Path);
            List<string> drop = null;
            foreach (KeyValuePair<string, Cached> pair in cache)
            {
                bool idle = !needed.Contains(pair.Key) && !inUse.Contains(pair.Key);
                pair.Value.Idle = idle && pair.Value.Tex != null;
                if (idle && pair.Value.Tex == null)
                {
                    if (drop == null) drop = new List<string>();
                    drop.Add(pair.Key); // запись о нечитаемом файле выключенного пака
                }
            }
            if (drop != null) foreach (string path in drop) Evict(path);
        }

        internal static long SpareBytes
        {
            get
            {
                long bytes = 0;
                foreach (Cached cached in cache.Values) if (cached.Idle && cached.Tex != null) bytes += cached.Bytes;
                return bytes;
            }
        }

        /// Отметка самой давней текстуры из запаса (long.MaxValue — запаса нет).
        internal static long OldestSpare
        {
            get
            {
                long oldest = long.MaxValue;
                foreach (Cached cached in cache.Values)
                    if (cached.Idle && cached.Tex != null && cached.Used < oldest) oldest = cached.Used;
                return oldest;
            }
        }

        internal static bool EvictOldestSpare()
        {
            string path = null;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<string, Cached> pair in cache)
                if (pair.Value.Idle && pair.Value.Tex != null
                    && pair.Value.Used < oldest) { oldest = pair.Value.Used; path = pair.Key; }
            if (path == null) return false;
            Evict(path);
            return true;
        }

        static void Evict(string path)
        {
            Cached cached;
            if (!cache.TryGetValue(path, out cached)) return;
            if (cached.Tex != null) UnityEngine.Object.Destroy(cached.Tex);
            cache.Remove(path);
        }

        static void FlushGraveyard()
        {
            foreach (Texture2D texture in graveyard) if (texture != null) UnityEngine.Object.Destroy(texture);
            graveyard.Clear();
        }
    }
}
