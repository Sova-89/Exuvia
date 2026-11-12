// ============================================================================
//  Engine.Background.cs — часть класса Engine: расшифровка атласов в фоновых потоках.
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
        // ---- расшифровка в фоне ------------------------------------------------

        /// Заявка на расшифровку в фоне. false — в фон отправить нельзя (вызывающий расшифрует по-старому).
        static bool StartLoad(string path, long len, long ticks, string origName, Texture template)
        {
            int w, h;
            if (!PngSizes.Get(path, len, ticks, out w, out h)) return false;
            if (w > SystemInfo.maxTextureSize || h > SystemInfo.maxTextureSize) return false;

            var load = new Loading();
            load.Path = path;
            load.OrigName = origName;
            load.Len = len;
            load.Ticks = ticks;
            load.Template = template;
            load.W = w;
            load.H = h;
            // Мип-уровни (уменьшенные копии) делаем, только если они есть у родной текстуры.
            load.Mips = template == null || template.mipmapCount > 1;

            if (loading.Count == 0 && batchClock == null)
            {
                batchClock = System.Diagnostics.Stopwatch.StartNew();
                batchCount = 0;
                batchFallbacks = 0;
                batchDecodeMs = batchUploadMs = batchWorstUploadMs = batchCreateMs = batchWorstCreateMs = 0;
            }
            loading[path] = load;
            toStart.Enqueue(load);
            return true;
        }

        /// Создаёт пустую текстуру нужного размера и отдаёт её память фоновому потоку.
        static unsafe bool Begin(Loading load)
        {
            Texture2D texture = null;
            try
            {
                // Пиксели заполнит фоновый поток, поэтому Unity не должен ни закрашивать текстуру, ни сразу
                // отправлять её (ещё пустую) в видеокарту: на атласе 4096x4096 это десятки миллисекунд.
                TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
                if (load.Mips) flags |= TextureCreationFlags.MipChain;
                texture = new Texture2D(load.W, load.H, GraphicsFormat.R8G8B8A8_SRGB, flags);
                texture.hideFlags = HideFlags.HideAndDontSave;

                NativeArray<byte> raw = texture.GetRawTextureData<byte>();
                long need = (long)load.W * load.H * 4;
                if (raw.Length < need) throw new InvalidOperationException("память текстуры меньше ожидаемой");

                var job = new DecodeJob();
                job.Path = load.Path;
                job.Dst = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(raw);
                job.DstLen = need;
                job.W = load.W;
                job.H = load.H;
                job.Tag = load;
                job.Owner = jobOwner;
                job.Done = Finish;

                load.Tex = texture;
                load.Started = true;
                DecodeJobs.Add(job);
                return true;
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                backgroundBroken = true;
                Plugin.Log.LogWarning("Exuvia: расшифровка в фоне не заработала (" + e.Message
                    + ") — читаю картинки по-старому, в главном потоке.");
                return false;
            }
        }

        /// Фоновый поток закончил: отправляем текстуру в видеокарту и кладём в кэш.
        static void Finish(DecodeJob job)
        {
            Loading load = job.Tag as Loading;
            if (load == null) return;
            Loading current;
            if (!loading.TryGetValue(load.Path, out current) || !ReferenceEquals(current, load))
            {
                // Задание уже не наше (отменили и завели заново).
                if (load.Tex != null) UnityEngine.Object.Destroy(load.Tex);
                return;
            }
            loading.Remove(load.Path);

            Cached old;
            Texture2D stale = cache.TryGetValue(load.Path, out old) ? old.Tex : null;

            if (job.Problem != null)
            {
                // Сами не смогли — пусть попробует Unity (он же напишет предупреждение, если файл и правда битый).
                if (load.Tex != null) UnityEngine.Object.Destroy(load.Tex);
                batchFallbacks++;
                Plugin.Log.LogInfo("Exuvia: " + ShortName(load.Path) + ": в фоне не вышло (" + job.Problem
                    + "), читаю в главном потоке.");
                DecodeSync(load.Path, load.Len, load.Ticks, load.OrigName, load.Template, stale, null);
                fresh = true;
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                load.Tex.Apply(load.Mips, true); // в видеокарту; копия в обычной памяти больше не нужна
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: " + ShortName(load.Path) + ": текстура не отправилась в видеокарту ("
                    + e.Message + ")");
                UnityEngine.Object.Destroy(load.Tex);
                var failed = new Cached();
                failed.Len = load.Len;
                failed.Ticks = load.Ticks;
                if (stale != null) { failed.Tex = stale; failed.Bytes = SizeOf(stale); } else failed.Failed = true;
                cache[load.Path] = failed;
                return;
            }
            double uploadMs = watch.Elapsed.TotalMilliseconds;

            Dress(load.Tex, load.OrigName, load.Template);
            var entry = new Cached();
            entry.Len = load.Len;
            entry.Ticks = load.Ticks;
            entry.Tex = load.Tex;
            entry.Used = Spare.Next();
            entry.Bytes = SizeOf(load.Tex);
            cache[load.Path] = entry;
            if (stale != null && stale != load.Tex) graveyard.Add(stale);
            fresh = true;

            // Материалы, которые ждали именно эту текстуру, получают её сразу — без обхода всех коллекций.
            for (int i = 0; i < load.Waiters.Count; i++)
            {
                Waiter waiter = load.Waiters[i];
                if (waiter.Mat == null) continue;
                string want = PackStore.Resolve(waiter.Collection, waiter.Orig);
                // скин уже выключили
                if (want == null || !string.Equals(want, load.Path, StringComparison.OrdinalIgnoreCase)) continue;
                Put(waiter.Mat, waiter.Id, waiter.Orig, load.Path, load.Tex);
            }

            batchCount++;
            batchDecodeMs += job.Ms;
            batchUploadMs += uploadMs;
            if (uploadMs > batchWorstUploadMs) batchWorstUploadMs = uploadMs;
            Plugin.Log.LogInfo("Exuvia: " + ShortName(load.Path) + " " + load.Tex.width + "x" + load.Tex.height
                + ", в фоне " + job.Ms.ToString("0")
                + " мс, в видеокарту " + uploadMs.ToString("0.0") + " мс" + (load.Mips ? ", с мип-уровнями" : ""));
        }

        /// Партия фоновой загрузки закончилась — итог в журнал.
        static void BatchEnd()
        {
            if (batchClock == null || loading.Count > 0) return;
            if (batchCount > 0 || batchFallbacks > 0)
                Plugin.Log.LogInfo("Exuvia: текстуры скинов загружены в фоне: " + batchCount + " шт. за "
                    + batchClock.ElapsedMilliseconds
                    + " мс (потоков до " + DecodeJobs.MaxThreads + "; расшифровка суммарно "
                        + batchDecodeMs.ToString("0") + " мс — в фоне; отправка в видеокарту суммарно "
                    + batchUploadMs.ToString("0") + " мс, самая долгая " + batchWorstUploadMs.ToString("0.0")
                        + " мс; создание пустых текстур суммарно "
                    + batchCreateMs.ToString("0") + " мс, самое долгое " + batchWorstCreateMs.ToString("0.0")
                        + " мс — это заминки игры)"
                    + (batchFallbacks > 0 ? "; по-старому прочитано: " + batchFallbacks : ""));
            batchClock = null;
        }

        /// Ждём фоновые потоки (только на загрузке сцены) и сразу отправляем готовое в видеокарту.
        static void WaitAll()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (;;)
            {
                Kick(double.MaxValue);
                if (DecodeJobs.PumpOne()) continue;
                if (loading.Count == 0) break;
                if (DecodeJobs.Outstanding == 0)
                {
                    if (DecodeJobs.HasDone || toStart.Count > 0) continue;
                    Abandon("фоновые потоки не вернули");
                    break;
                }
                if (watch.ElapsedMilliseconds > WaitAllLimitMs)
                {
                    Plugin.Log.LogWarning("Exuvia: расшифровка в фоне идёт дольше " + (WaitAllLimitMs / 1000)
                        + " с — сцена откроется, не дожидаясь; текстуры наденутся по готовности.");
                    break;
                }
                DecodeJobs.WaitDone(20);
            }
            BatchEnd();
        }

        /// Убираем заявки, к которым фоновые потоки ещё не приступили (нужное Apply закажет заново).
        static void CancelQueued()
        {
            List<DecodeJob> dropped = DecodeJobs.DropQueued(jobOwner);
            for (int i = 0; i < dropped.Count; i++)
            {
                Loading load = dropped[i].Tag as Loading;
                if (load == null) continue;
                Loading current;
                if (loading.TryGetValue(load.Path, out current) && ReferenceEquals(current, load))
                    loading.Remove(load.Path);
                if (load.Tex != null) UnityEngine.Object.Destroy(load.Tex);
            }
            while (toStart.Count > 0)
            {
                Loading load = toStart.Dequeue();
                Loading current;
                if (loading.TryGetValue(load.Path, out current) && ReferenceEquals(current, load))
                    loading.Remove(load.Path);
            }
        }

        static void Abandon(string why)
        {
            Plugin.Log.LogWarning("Exuvia: " + why + " текстур: " + loading.Count
                + " — попробую загрузить их заново.");
            // Сами текстуры не уничтожаем: вдруг поток всё-таки ещё пишет в их память.
            loading.Clear();
            toStart.Clear();
        }

        /// Перед выходом из игры: дождаться потоков, которые ещё пишут в память текстур.
        internal static void Shutdown()
        {
            DecodeJobs.Shutdown(1500);
        }
    }
}
