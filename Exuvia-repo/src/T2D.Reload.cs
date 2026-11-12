// ============================================================================
//  T2D.Reload.cs — часть класса T2D: запас готовых картинок, перечитывание паков, поиск CustomizerT2D.
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
        // ---- запас готовых картинок (для Spare.cs) ------------------------------------

        static void TrimCopies()
        {
            Spare.Trim();
        }

        static bool Evictable(SkinCopy copy)
        {
            return copy.Ready && copy.Waiting.Count == 0 && transfers.Count == 0;
        }

        internal static long SpareBytes
        {
            get { return CopiesBytes(); }
        }

        internal static long OldestSpare
        {
            get
            {
                long oldest = long.MaxValue;
                foreach (SkinCopy copy in copies.Values) if (Evictable(copy) && copy.Used < oldest) oldest = copy.Used;
                return oldest;
            }
        }

        internal static bool EvictOldestSpare()
        {
            SkinCopy pick = null;
            foreach (SkinCopy copy in copies.Values)
                if (Evictable(copy) && (pick == null || copy.Used < pick.Used)) pick = copy;
            if (pick == null) return false;
            DropCopy(pick);
            return true;
        }

        // ---- перечитать паки ------------------------------------------------------

        /// Просит CustomizerT2D перечитать паки и возвращает родной вид текстурам, которые больше
        /// никто не подменяет. null — всё хорошо, иначе текст ошибки.
        internal static string Reload()
        {
            try
            {
                if (own) return ReloadOwn();
                Type type = FindType();
                if (type == null) return L.T("CustomizerT2D не найден", "CustomizerT2D not found");
                Hook();

                PropertyInfo prop = type.GetProperty("Instance", Any);
                MethodInfo reload = type.GetMethod("ForceReloadAllTextures", Any);
                if (prop == null || reload == null)
                    return L.T("В CustomizerT2D нет нужных методов (другая версия?)",
                        "CustomizerT2D has no expected methods (other version?)");

                object instance = prop.GetValue(null, null);
                UnityEngine.Object unityObject = instance as UnityEngine.Object;
                if (instance == null || unityObject == null)
                    return L.T("CustomizerT2D ещё не запущен", "CustomizerT2D is not running yet");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                int swapsBefore = SwapCount;
                reload.Invoke(instance, null);
                long reloadMs = clock.ElapsedMilliseconds;
                if (hooked) RestoreUnwanted(keysField.GetValue(instance) as HashSet<string>);
                Plugin.Log.LogInfo("Exuvia: меню и интерфейс перечитаны за " + clock.ElapsedMilliseconds
                    + " мс (подменено сразу: "
                    + (SwapCount - swapsBefore) + ", ждут переноса: " + transfers.Count + ", расшифровываются в фоне: "
                        + copiesLoading
                    + ", возврат родных: " + (clock.ElapsedMilliseconds - reloadMs) + " мс).");
                return null;
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                Plugin.Log.LogWarning("Exuvia: не удалось перечитать картинки меню и интерфейса: " + inner);
                return inner.Message;
            }
        }

        /// Копии текстур, которые игра уже выгрузила (сменилась сцена), больше не нужны.
        internal static void Prune()
        {
            if (saved.Count == 0) return;
            foreach (KeyValuePair<int, Saved> pair in new List<KeyValuePair<int, Saved>>(saved))
            {
                if (pair.Value.Tex != null) continue;
                if (pair.Value.Copy != null) UnityEngine.Object.Destroy(pair.Value.Copy);
                saved.Remove(pair.Key);
            }
        }

        /// Текстуры, которые подменял выключенный пак, получают родное содержимое обратно.
        static void RestoreUnwanted(HashSet<string> keys)
        {
            int exact = 0, converted = 0, failed = 0, gone = 0;
            foreach (KeyValuePair<int, Saved> pair in new List<KeyValuePair<int, Saved>>(saved))
            {
                Saved backup = pair.Value;
                if (backup.Tex == null)
                {
                    // игра сама выгрузила текстуру (сменилась сцена) — копия больше не нужна
                    gone++;
                    if (backup.Copy != null) UnityEngine.Object.Destroy(backup.Copy);
                    saved.Remove(pair.Key);
                    continue;
                }
                if (!backup.Applied) continue;                            // в ней и так родная картинка
                if (keys != null && keys.Contains(backup.Key)) continue;  // её всё ещё подменяет включённый пак

                bool back = false;
                try
                {
                    // Скин только что выключили: его готовая картинка теперь самая свежая в запасе.
                    SkinCopy shown;
                    if (backup.ShownPath != null && copies.TryGetValue(backup.ShownPath, out shown))
                        shown.Used = Spare.Next();
                    back = RestoreOne(backup);
                    if (back) exact++; else converted++;
                    backup.Applied = false;
                    backup.ShownPath = null;
                }
                catch (Exception e)
                {
                    failed++;
                    Plugin.Log.LogWarning("Exuvia: текстура " + backup.Tex.name + " не вернулась к родной: "
                        + e.Message);
                }
                if (back)
                {
                    // Текстура снова точь-в-точь родная: копию можно отпустить, при надобности снимем новую.
                    if (backup.Copy != null) UnityEngine.Object.Destroy(backup.Copy);
                    saved.Remove(pair.Key);
                }
                // Иначе копию держим: в текстуре родная картинка, но уже в несжатом виде, и следующую копию
                // пришлось бы снимать с неё (она в несколько раз тяжелее родной сжатой).
            }
            if (exact + converted + failed > 0)
                Plugin.Log.LogInfo("Exuvia: родной вид возвращён текстурам: " + (exact + converted)
                    + (converted > 0 ? " (из них в несжатом виде: " + converted + ")" : "")
                    + (failed > 0 ? ", не вышло: " + failed : "")
                    + (gone > 0 ? "; уже выгружено игрой: " + gone : "")
                    + "; копий в памяти: " + saved.Count);
        }

        /// true — текстура снова точно такая, как была; false — содержимое вернули, но в несжатом виде.
        static bool RestoreOne(Saved backup)
        {
            Texture2D tex = backup.Tex, copy = backup.Copy;
            if (copy == null) throw new Exception("копия пропала");

            // После подмены текстура стала несжатой. Возвращаем ей родной вид (сжатие, число уровней) —
            // это возможно, пока у неё есть копия в обычной памяти (CustomizerT2D её оставляет).
            bool same = tex.width == copy.width && tex.height == copy.height
                && tex.graphicsFormat == copy.graphicsFormat && tex.mipmapCount == copy.mipmapCount;
            // При быстрой подмене текстуру обратно не сжимаем: иначе при следующем включении скина её
            // пришлось бы снова превращать в несжатую (заминка), а так картинки меняются копированием.
            bool keepPlain = Fast && !fastBroken && backup.ShownPath != null;
            if (!same && tex.isReadable && !keepPlain)
            {
                if (tex.Reinitialize(copy.width, copy.height, copy.graphicsFormat, copy.mipmapCount > 1))
                {
                    tex.Apply(false, !backup.WasReadable);
                    same = tex.graphicsFormat == copy.graphicsFormat && tex.mipmapCount == copy.mipmapCount;
                }
            }
            if (same)
            {
                Graphics.CopyTexture(copy, tex);
                return true;
            }
            if (!Graphics.ConvertTexture(copy, tex)) throw new Exception("видеокарта не смогла скопировать");
            return false;
        }

        // ---- поиск CustomizerT2D --------------------------------------------------

        static Type cached;

        static Type FindType()
        {
            if (cached != null) return cached;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    Type type = asm.GetType(TypeName, false);
                    if (type != null)
                    {
                        cached = type;
                        return type;
                    }
                }
                catch (Exception)
                {
                    // сборки, которые не дают прочитать типы, пропускаем
                }
            }
            return null;
        }
    }
}
