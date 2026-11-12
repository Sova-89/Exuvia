// ============================================================================
//  T2D.Own.cs — часть класса T2D: свой режим — подмена без CustomizerT2D.
//  Общее описание — в начале T2D.cs.
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Exuvia
{
    internal static partial class T2D
    {
        // ---- свой режим: без CustomizerT2D ----------------------------------------

        /// Включает свой режим, если CustomizerT2D в игре нет. Звать из Start (когда все моды уже загружены).
        internal static void EnableOwn()
        {
            if (own || FindType() != null) return;
            own = true;
            hooked = true; // возврат родного вида — наш собственный
            replacedIds = new HashSet<int>();
            notOurs = new HashSet<int>();
            try
            {
                const BindingFlags Mine = BindingFlags.Static | BindingFlags.NonPublic;
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".t2d.own");
                PatchOwn(harmony, typeof(Material).GetProperty("mainTexture").GetSetMethod(),
                    typeof(T2D).GetMethod("OwnTexture", Mine), "Material.mainTexture");
                PatchOwn(harmony, typeof(Material).GetMethod("SetTexture", new Type[] { typeof(string),
                    typeof(Texture) }), typeof(T2D).GetMethod("OwnTexture", Mine), "Material.SetTexture");
                PatchOwn(harmony, typeof(SpriteRenderer).GetProperty("sprite").GetSetMethod(),
                    typeof(T2D).GetMethod("OwnSprite", Mine), "SpriteRenderer.sprite");
                PatchOwn(harmony, typeof(UnityEngine.UI.Image).GetProperty("sprite").GetSetMethod(),
                    typeof(T2D).GetMethod("OwnSprite", Mine), "Image.sprite");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехваты для меню и интерфейса не поставились (" + e.Message
                    + ") — текстуры будут находиться только обходом.");
            }
            BuildOwn();
            Plugin.Log.LogInfo("Exuvia: CustomizerT2D не найден — меню и интерфейс скинов (папки Texture2D) подменяю "
                + "сам; картинок во включённых скинах: " + ownKeys.Count + ".");
            if (ownKeys.Count > 0) ScanAll();
        }

        static void PatchOwn(HarmonyLib.Harmony harmony, MethodBase target, MethodInfo postfix, string what)
        {
            try
            {
                if (target == null || postfix == null) throw new Exception("не найден");
                harmony.Patch(target, null, new HarmonyLib.HarmonyMethod(postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехват " + what + " не поставился (" + e.Message + ").");
            }
        }

        // Перехваты: материалу назначили текстуру / рисовальщику или картинке интерфейса назначили спрайт.
        static void OwnTexture(Texture value)
        {
            if (ownKeys.Count == 0) return;
            Texture2D tex = value as Texture2D;
            if (tex != null) OwnTry(tex);
        }

        static void OwnSprite(Sprite value)
        {
            if (ownKeys.Count == 0 || value == null) return;
            try
            {
                Texture2D tex = value.texture;
                if (tex != null) OwnTry(tex);
            }
            catch (Exception) { }
        }

        /// Составляет список картинок включённых паков (то же делает BuildPrefix для CustomizerT2D).
        static void BuildOwn()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var recs = new Dictionary<string, FileRec>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Collect(PackStore.TextureKey, recs, sizes);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: список картинок меню и интерфейса не составился: " + e.Message);
            }
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, FileRec> pair in recs) paths[pair.Key] = pair.Value.Path;
            lazyPaths = paths;
            lazyRecs = recs;
            ownSizes = sizes;
            ownKeys = new HashSet<string>(paths.Keys, StringComparer.OrdinalIgnoreCase);
            if (clock.ElapsedMilliseconds >= 30 || ownKeys.Count > 0)
                Plugin.Log.LogInfo("Exuvia: список картинок меню и интерфейса: " + ownKeys.Count + " шт. за "
                    + clock.ElapsedMilliseconds + " мс (без расшифровки).");
        }

        /// Текстура игры попалась на глаза: если во включённом скине есть картинка для неё — подменяем.
        static void OwnTry(Texture2D tex)
        {
            if (inOwn) return;
            inOwn = true;
            try
            {
                int id = tex.GetInstanceID();
                if (replacedIds.Contains(id) || notOurs.Contains(id)) return; // уже подменена или не нужна
                if (replacedIds.Count > ForgetAfter || notOurs.Count > ForgetAfter)
                {
                    replacedIds.Clear();
                    notOurs.Clear();
                }

                string key = PackStore.TextureKey(tex.name);
                FileRec rec;
                int[] size;
                if (key == null || !lazyRecs.TryGetValue(key, out rec) || !ownSizes.TryGetValue(key, out size)
                    || tex.width != size[0] || tex.height != size[1])
                {
                    notOurs.Add(id); // картинки для неё нет или размер не тот
                    return;
                }

                // Родное содержимое сохраняем один раз на всю жизнь текстуры.
                Saved backup;
                if (!saved.TryGetValue(id, out backup) && !backupBroken)
                {
                    try
                    {
                        Backup(tex, id, key);
                        saved.TryGetValue(id, out backup);
                    }
                    catch (Exception e)
                    {
                        if (failedCopies < 5)
                            Plugin.Log.LogWarning("Exuvia: копия родной текстуры " + tex.name + " не снялась ("
                                + e.Message + ") — она вернётся к обычной только после перезапуска игры.");
                        failedCopies++;
                    }
                }

                if (backup != null)
                {
                    if (!(Fast && !fastBroken && FastSwap(backup, rec))) OldSwap(backup, rec.Path, rec.Len, rec.Ticks);
                    replacedIds.Add(id);
                }
                else
                {
                    // Без копии родного: подменяем как CustomizerT2D, вернуть будет нечем до перезапуска игры.
                    bool ok = false;
                    try { ok = ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(rec.Path), false); }
                    catch (Exception) { }
                    if (ok) { replacedIds.Add(id); SwapCount++; }
                    else notOurs.Add(id);
                }
            }
            catch (Exception e)
            {
                if (transferWarnings++ < 5) Plugin.Log.LogWarning("Exuvia: подмена текстуры меню и интерфейса не "
                    + "вышла: " + e.Message);
            }
            finally
            {
                inOwn = false;
            }
        }

        /// Обход всех текстур игры разом (перечитывание паков, загрузка сцены).
        static void ScanAll()
        {
            Texture2D[] all = Resources.FindObjectsOfTypeAll<Texture2D>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null) OwnTry(all[i]);
            scan = null;
        }

        /// Сцена загрузилась (или игра догрузила что-то чуть позже): ищем новые текстуры сразу.
        internal static void Rescan()
        {
            if (!own || ownKeys.Count == 0) return;
            try { ScanAll(); }
            catch (Exception e) { Plugin.Log.LogWarning("Exuvia: обход текстур меню и интерфейса не удался: "
                + e.Message); }
        }

        /// Понемногу обходим все текстуры игры: вдруг какая-то появилась мимо перехватов.
        static void OwnScanTick()
        {
            float now = Time.unscaledTime;
            if (now < nextScanTick) return;
            nextScanTick = now + ScanTick;
            if (scan == null)
            {
                if (now - snapshotAt < SnapshotEvery) return;
                scan = Resources.FindObjectsOfTypeAll<Texture2D>();
                scanAt = 0;
                snapshotAt = now;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (scanAt < scan.Length)
            {
                Texture2D tex = scan[scanAt++];
                if (tex != null) OwnTry(tex);
                if (watch.Elapsed.TotalMilliseconds >= ScanBudgetMs) break;
            }
            if (scanAt >= scan.Length) scan = null;
        }

        /// Перечитать паки в своём режиме: новый список картинок, обход всех текстур, возврат родного вида ненужным.
        static string ReloadOwn()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int swapsBefore = SwapCount;
            replacedIds.Clear();
            notOurs.Clear();
            BuildOwn();
            if (ownKeys.Count > 0) ScanAll();
            long reloadMs = clock.ElapsedMilliseconds;
            RestoreUnwanted(ownKeys);
            Plugin.Log.LogInfo("Exuvia: меню и интерфейс перечитаны за " + clock.ElapsedMilliseconds
                + " мс (подменено сразу: "
                + (SwapCount - swapsBefore) + ", ждут переноса: " + transfers.Count + ", расшифровываются в фоне: "
                    + copiesLoading
                + ", возврат родных: " + (clock.ElapsedMilliseconds - reloadMs) + " мс).");
            return null;
        }
    }
}
