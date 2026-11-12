// ============================================================================
//  T2D.Bridge.cs — часть класса T2D: мост к CustomizerT2D (перехваты его методов).
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
        // ---- перехваты CustomizerT2D ----------------------------------------------

        /// Ставит перехваты. Звать после того, как PackStore прочитал паки; можно звать повторно.
        internal static void Hook()
        {
            if (own || hooked || hookTried) return;
            Type type = FindType();
            if (type == null) return; // CustomizerT2D ещё не загружен или его нет
            hookTried = true;
            try
            {
                keysField = type.GetField("_keys", Any);
                mapField = type.GetField("_map", Any);
                normalize = type.GetMethod("NormalizeName", Any);
                MethodInfo dirs = type.GetMethod("GetTexturePackDirsToScan", Any);
                MethodInfo swap = type.GetMethod("TrySwap", Any);
                if (keysField == null || mapField == null || normalize == null || dirs == null || swap == null
                    || type.GetField("_replacedIds", Any) == null || type.GetField("_notOurTex", Any) == null)
                    throw new Exception("нет ожидаемых полей или методов (другая версия?)");

                const BindingFlags Mine = BindingFlags.Static | BindingFlags.NonPublic;
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".t2d");
                harmony.Patch(dirs, new HarmonyLib.HarmonyMethod(typeof(T2D).GetMethod("DirsPrefix", Mine)));
                harmony.Patch(swap, new HarmonyLib.HarmonyMethod(typeof(T2D).GetMethod("SwapPrefix", Mine)),
                    new HarmonyLib.HarmonyMethod(typeof(T2D).GetMethod("SwapPostfix", Mine)));
                hooked = true;
                Plugin.Log.LogInfo("Exuvia: меню и интерфейс из скинов (Texture2D) теперь выключаются без "
                    + "перезапуска игры.");

                // Список картинок паков составляем сами (без расшифровки каждой картинки).
                try
                {
                    MethodInfo build = type.GetMethod("BuildOverrideMap", Any);
                    if (build == null) throw new Exception("нет BuildOverrideMap");
                    harmony.Patch(build, new HarmonyLib.HarmonyMethod(typeof(T2D).GetMethod("BuildPrefix", Mine)),
                        new HarmonyLib.HarmonyMethod(typeof(T2D).GetMethod("BuildPostfix", Mine)));
                }
                catch (Exception e)
                {
                    lazyBroken = true;
                    Plugin.Log.LogWarning("Exuvia: быстрое чтение картинок меню и интерфейса не включилось: "
                        + e.Message);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехват CustomizerT2D не поставился (" + e.Message
                    + ") — меню и интерфейс скина будут выключаться только после перезапуска игры.");
            }
        }

        /// Вместо BuildOverrideMap у CustomizerT2D. Делает то же самое (ключ = нормализованное имя файла,
        /// значение = байты, ширина, высота; поздний пак перекрывает ранний), но без расшифровки картинок:
        /// размеры берём из заголовка PNG, а байты подставляем позже, прямо перед подменой (см. SwapPrefix).
        /// false — оригинал не выполнять.
        static bool BuildPrefix(object __instance, out System.Diagnostics.Stopwatch __state)
        {
            __state = System.Diagnostics.Stopwatch.StartNew();
            if (lazyBroken) { lazyPaths = null; lazyRecs = null; return true; }
            try
            {
                Type mapType = mapField.FieldType;
                Type tupleType = mapType.GetGenericArguments()[1];
                var map = (IDictionary)Activator.CreateInstance(mapType,
                    new object[] { StringComparer.OrdinalIgnoreCase });
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var recs = new Dictionary<string, FileRec>(StringComparer.OrdinalIgnoreCase);
                var sizes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                object instance = __instance;
                Collect(delegate (string name) { return Normalize(instance, name); }, recs, sizes);
                foreach (KeyValuePair<string, FileRec> pair in recs)
                {
                    int[] size = sizes[pair.Key];
                    map[pair.Key] = Activator.CreateInstance(tupleType, new object[] { NoBytes, size[0], size[1] });
                    paths[pair.Key] = pair.Value.Path;
                }

                mapField.SetValue(__instance, map);
                keysField.SetValue(__instance, new HashSet<string>(paths.Keys, StringComparer.OrdinalIgnoreCase));
                lazyPaths = paths;
                lazyRecs = recs;
                return false;
            }
            catch (Exception e)
            {
                lazyBroken = true;
                lazyPaths = null;
                lazyRecs = null;
                Plugin.Log.LogWarning("Exuvia: быстрое чтение картинок меню и интерфейса не сработало (" + e.Message
                    + ") — дальше их читает сам CustomizerT2D.");
                return true;
            }
        }

        /// Список картинок Texture2D включённых паков: ключ -> файл и размер. Папка читается без вложенных,
        /// паки по алфавиту, поздний перекрывает ранний (как у CustomizerT2D).
        static void Collect(Func<string, string> keyOf, Dictionary<string, FileRec> recs,
            Dictionary<string, int[]> sizes)
        {
            var packs = new List<PackInfo>();
            foreach (PackInfo pack in PackStore.Packs)
                if (pack.Enabled && !string.IsNullOrEmpty(pack.Dir)) packs.Add(pack);
            packs.Sort(delegate (PackInfo x, PackInfo y) { return StringComparer.OrdinalIgnoreCase.Compare(x.Dir,
                y.Dir); });

            foreach (PackInfo pack in packs)
            {
                string folder = System.IO.Path.Combine(pack.Dir, "Texture2D");
                foreach (FileRec rec in FileLister.List(folder, ".png"))
                {
                    string key = keyOf(System.IO.Path.GetFileNameWithoutExtension(rec.Path));
                    if (key == null) continue;
                    int w, h;
                    if (!PngSizes.Get(rec.Path, rec.Len, rec.Ticks, out w, out h)
                        && !SizeByDecoding(rec.Path, out w, out h)) continue;
                    recs[key] = rec;
                    sizes[key] = new int[] { w, h };
                }
            }
        }

        /// Запасной способ узнать размер: файл с расширением .png оказался не PNG (например, JPG).
        /// CustomizerT2D такие принимает, поэтому и мы не отбрасываем — расшифровываем, как делал он.
        static bool SizeByDecoding(string path, out int w, out int h)
        {
            w = 0;
            h = 0;
            Texture2D probe = null;
            try
            {
                probe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(probe, System.IO.File.ReadAllBytes(path), false)) return false;
                w = probe.width;
                h = probe.height;
                return w > 0 && h > 0;
            }
            catch (Exception) { return false; }
            finally
            {
                if (probe != null) UnityEngine.Object.Destroy(probe);
            }
        }

        static void BuildPostfix(object __instance, System.Diagnostics.Stopwatch __state)
        {
            try
            {
                if (__state == null) return;
                long ms = __state.ElapsedMilliseconds;
                var keys = keysField.GetValue(__instance) as HashSet<string>;
                int count = keys != null ? keys.Count : 0;
                if (ms >= 30 || count > 0)
                    Plugin.Log.LogInfo("Exuvia: список картинок меню и интерфейса: " + count + " шт. за " + ms + " мс"
                        + (lazyPaths != null ? " (без расшифровки)." : " (читал сам CustomizerT2D)."));
            }
            catch (Exception) { }
        }

        /// Какие паки смотреть CustomizerT2D: только включённые. Его собственное правило
        /// «ни один не включён — беру все» нам не подходит.
        static bool DirsPrefix(ref List<string> __result)
        {
            try
            {
                var list = new List<string>();
                foreach (PackInfo pack in PackStore.Packs)
                    if (pack.Enabled && !string.IsNullOrEmpty(pack.Dir)) list.Add(pack.Dir);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                __result = list;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// Перед подменой текстуры игры: снимаем с неё копию и либо подменяем сами (быстрый путь, оригинал
        /// не выполняется), либо подставляем байты картинки пака для CustomizerT2D (старый путь).
        /// Вызывается очень часто (на каждую текстуру, которую трогает игра), поэтому сначала самые дешёвые
        /// проверки — те же, что делает сам CustomizerT2D. __state — ключ, для которого подставили байты.
        static bool SwapPrefix(object __instance, Texture2D tex, ref bool __result, out string __state)
        {
            __state = null;
            if (backupBroken) return true;
            try
            {
                if (tex == null) return true;
                int id = tex.GetInstanceID();
                if (!ReferenceEquals(owner, __instance))
                {
                    owner = __instance;
                    Type type = __instance.GetType();
                    replacedIds = type.GetField("_replacedIds", Any).GetValue(__instance) as HashSet<int>;
                    notOurs = type.GetField("_notOurTex", Any).GetValue(__instance) as HashSet<int>;
                }
                if (replacedIds != null && replacedIds.Contains(id)) return true; // уже подменена
                if (notOurs != null && notOurs.Contains(id)) return true;         // паку не нужна

                var keys = keysField.GetValue(__instance) as HashSet<string>;
                if (keys == null || keys.Count == 0) return true;
                string key = Normalize(__instance, tex.name);
                if (key == null || !keys.Contains(key)) return true;

                // Размер картинки пака должен совпасть с текстурой, иначе подмены не будет.
                IDictionary map = mapField.GetValue(__instance) as IDictionary;
                object entry = map != null && map.Contains(key) ? map[key] : null;
                if (entry == null) return true;
                Type tuple = entry.GetType();
                int w = (int)tuple.GetField("Item2").GetValue(entry);
                int h = (int)tuple.GetField("Item3").GetValue(entry);
                if (tex.width != w || tex.height != h) return true;

                // Подмена состоится. Родное содержимое текстуры сохраняем один раз на всю её жизнь.
                Saved backup;
                if (!saved.TryGetValue(id, out backup))
                {
                    try
                    {
                        Backup(tex, id, key);
                        saved.TryGetValue(id, out backup);
                    }
                    catch (Exception e)
                    {
                        // Одна текстура не скопировалась — остальные это не затрагивает.
                        if (failedCopies < 5)
                            Plugin.Log.LogWarning("Exuvia: копия родной текстуры " + tex.name + " не снялась ("
                                + e.Message + ") — она вернётся к обычной только после перезапуска игры.");
                        failedCopies++;
                    }
                }

                // Быстрый путь: расшифровка в фоне, в текстуру игры переносим готовое.
                FileRec rec;
                if (backup != null && Fast && !fastBroken && replacedIds != null && lazyRecs != null
                    && lazyRecs.TryGetValue(key, out rec) && FastSwap(backup, rec))
                {
                    replacedIds.Add(id);
                    __result = true;
                    return false;
                }

                // Старый путь: картинку расшифрует и запишет в текстуру сам CustomizerT2D.
                swapClock = System.Diagnostics.Stopwatch.StartNew();
                // Байты картинки читаем только сейчас, когда текстура действительно есть в игре.
                string path;
                if (lazyPaths != null && lazyPaths.TryGetValue(key, out path))
                {
                    byte[] bytes;
                    try { bytes = System.IO.File.ReadAllBytes(path); }
                    catch (Exception) { bytes = NoBytes; } // CustomizerT2D сам отметит, что подмена не вышла
                    map[key] = Activator.CreateInstance(tuple, new object[] { bytes, w, h });
                    __state = key;
                }
                if (backup != null)
                {
                    backup.Applied = true;
                    backup.ShownPath = null; // что именно в текстуре, по-старому не отслеживаем
                }
            }
            catch (Exception e)
            {
                backupBroken = true;
                Plugin.Log.LogWarning("Exuvia: не удалось сохранить родную текстуру перед подменой (" + e.Message
                    + ") — меню и интерфейс скина будут выключаться только после перезапуска игры.");
            }
            return true;
        }

        static System.Diagnostics.Stopwatch swapClock;

        /// После подмены: байты картинки больше не нужны — не держим их в памяти.
        static void SwapPostfix(object __instance, string __state, bool __result)
        {
            try
            {
                if (swapClock != null)
                {
                    if (__result)
                    {
                        SwapCount++;
                        SwapMs += swapClock.Elapsed.TotalMilliseconds;
                    }
                    swapClock = null;
                }
                if (__state == null) return;
                IDictionary map = mapField.GetValue(__instance) as IDictionary;
                object entry = map != null && map.Contains(__state) ? map[__state] : null;
                if (entry == null) return;
                Type tuple = entry.GetType();
                map[__state] = Activator.CreateInstance(tuple, new object[] { NoBytes,
                    tuple.GetField("Item2").GetValue(entry), tuple.GetField("Item3").GetValue(entry) });
            }
            catch (Exception) { }
        }

        static string Normalize(object instance, string name)
        {
            object result = normalize.Invoke(normalize.IsStatic ? null : instance, new object[] { name });
            return result as string;
        }

        /// Копия текстуры в видеопамяти в том же виде, что и оригинал (сжатая — значит сжатая).
        static void Backup(Texture2D tex, int id, string key)
        {
            bool readable = tex.isReadable;
            Texture2D copy = CloneGpu(tex, "Exuvia backup " + tex.name, readable);
            saved[id] = new Saved { Tex = tex, Copy = copy, Key = key, WasReadable = readable, Applied = false };
        }

        /// Создаёт текстуру того же вида и копирует в неё содержимое внутри видеокарты.
        static Texture2D CloneGpu(Texture2D tex, string name, bool keepReadable)
        {
            int mips = Math.Max(1, tex.mipmapCount);
            // Создаём по виду текстуры (TextureFormat), а не по её внутреннему формату: не каждый внутренний
            // формат можно создать напрямую. «Crunch»-сжатие существует только на диске: в видеопамяти
            // такая текстура лежит как обычная DXT/ETC — такой и делаем копию.
            TextureFormat format = tex.format;
            if (format == TextureFormat.DXT1Crunched) format = TextureFormat.DXT1;
            else if (format == TextureFormat.DXT5Crunched) format = TextureFormat.DXT5;
            else if (format == TextureFormat.ETC_RGB4Crunched) format = TextureFormat.ETC_RGB4;
            else if (format == TextureFormat.ETC2_RGBA8Crunched) format = TextureFormat.ETC2_RGBA8;
            var copy = new Texture2D(tex.width, tex.height, format, mips, !tex.isDataSRGB);
            if (copy == null) throw new Exception("видеокарта не создала текстуру вида " + format);
            try
            {
                // Копировать в видеопамяти можно только между текстурами одного вида. Не совпало — лучше
                // остаться без копии, чем потом вернуть в текстуру мусор.
                if (copy.graphicsFormat != tex.graphicsFormat || copy.mipmapCount != tex.mipmapCount)
                    throw new Exception("копия получилась другого вида: " + copy.graphicsFormat + "/" + copy.mipmapCount
                        + " вместо " + tex.graphicsFormat + "/" + tex.mipmapCount);
                copy.name = name;
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.Apply(false, !keepReadable);
                Graphics.CopyTexture(tex, copy);
            }
            catch (Exception)
            {
                UnityEngine.Object.Destroy(copy);
                throw;
            }
            return copy;
        }
    }
}
