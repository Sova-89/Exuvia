// ============================================================================
//  SpriteEngine.Sheets.cs — часть класса SpriteEngine: сборка листов в фоне и расшифровка картинок.
//  Общее описание — в начале SpriteEngine.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace Exuvia
{
    internal static partial class SpriteEngine
    {
        // ---- сборка листов в фоне ----------------------------------------------

        /// Создаёт пустые листы и раздаёт их фоновым потокам (по заданию на лист).
        /// false — не вышло, листы надо собирать по-старому.
        static unsafe bool StartBackground(Build build, Packer.Result result)
        {
            var pages = new List<Texture2D>();
            var jobs = new List<DecodeJob>();
            // Листы самой Хорнет собираем раньше остальных: её видно сразу.
            bool urgent = string.Equals(build.Collection, "Knight", StringComparison.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < result.Pages.Count; i++)
                {
                    Packer.Page page = result.Pages[i];
                    // Память листа заполнит фоновый поток, поэтому Unity не должен ни закрашивать её,
                    // ни сразу отправлять пустой лист в видеокарту.
                    var texture = new Texture2D(page.W, page.H, GraphicsFormat.R8G8B8A8_SRGB,
                        TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
                    pages.Add(texture);
                    texture.name = "Exuvia " + build.Collection + " " + i;
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    texture.wrapMode = TextureWrapMode.Clamp;

                    NativeArray<byte> raw = texture.GetRawTextureData<byte>();
                    long need = (long)page.W * page.H * 4;
                    if (raw.Length < need) throw new InvalidOperationException("память листа меньше ожидаемой");

                    var pageJob = new PageJob();
                    pageJob.B = build;
                    pageJob.Page = i;
                    pageJob.W = page.W;
                    pageJob.H = page.H;
                    pageJob.Ptr = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(raw);
                    pageJob.Len = need;

                    var job = new DecodeJob();
                    job.Tag = pageJob;
                    job.Owner = build;
                    job.Work = PageWork;
                    job.Done = PageDone;
                    job.Urgent = urgent;
                    jobs.Add(job);
                }
                foreach (Slot slot in build.Queue) ((PageJob)jobs[slot.Page].Tag).Slots.Add(slot);
            }
            catch (Exception e)
            {
                foreach (Texture2D texture in pages) if (texture != null) UnityEngine.Object.Destroy(texture);
                backgroundBroken = true;
                Plugin.Log.LogWarning("Exuvia: сборка листов спрайтов в фоне не заработала (" + e.Message
                    + ") — собираю по-старому, в главном потоке.");
                return false;
            }

            foreach (Texture2D texture in pages)
            {
                build.Pages.Add(texture);
                build.Pixels += (long)texture.width * texture.height;
            }
            build.JobsOut = jobs.Count;
            jobsOut += jobs.Count;
            foreach (DecodeJob job in jobs) DecodeJobs.Add(job);
            return true;
        }

        /// Работа фонового потока: очистить лист и расшифровать в него все его картинки.
        static unsafe void PageWork(DecodeJob job)
        {
            PageJob pageJob = (PageJob)job.Tag;
            Build build = pageJob.B;
            byte* page = (byte*)pageJob.Ptr;
            long stride = (long)pageJob.W * 4;
            Png.Clear(page, pageJob.Len); // прозрачный лист
            for (int i = 0; i < pageJob.Slots.Count; i++)
            {
                if (build.Cancel) return;
                Slot slot = pageJob.Slots[i];
                try
                {
                    int len;
                    byte[] f = Png.ReadFile(slot.File.Path, out len);
                    PngHead head = Png.Parse(f, len);
                    if (head.Problem == null && head.Supported && head.Width == slot.W && head.Height == slot.H
                        && slot.X >= 0 && slot.Y >= 0 && slot.X + slot.W <= pageJob.W && slot.Y + slot.H <= pageJob.H)
                    {
                        long off = ((long)slot.Y * pageJob.W + slot.X) * 4;
                        if (Png.Decode(f, head, page + off, stride, pageJob.Len - off) == null) slot.Done = true;
                    }
                }
                catch (Exception)
                {
                    // эту картинку дочитает главный поток (он же напишет, если файл и правда плохой)
                }
                System.Threading.Interlocked.Increment(ref build.DoneCount);
            }
        }

        /// Фоновый поток закончил лист (зовётся в главном потоке).
        static void PageDone(DecodeJob job)
        {
            PageJob pageJob = (PageJob)job.Tag;
            Build build = pageJob.B;
            build.JobsOut--;
            jobsOut--;
            if (build.Cancel || build.Disposed) return;

            if (job.Problem != null)
            {
                // Лист целиком не собрался: чистим его и читаем все его картинки по-старому.
                Plugin.Log.LogWarning("Exuvia: " + build.Collection + ": лист " + pageJob.Page
                    + " в фоне не собрался (" + job.Problem + ") — собираю в главном потоке.");
                build.Pages[pageJob.Page].SetPixels32(new Color32[pageJob.W * pageJob.H]);
                foreach (Slot slot in pageJob.Slots) slot.Done = false;
            }
            if (build.JobsOut == 0) FinishBuild(build);
        }

        /// Все листы сборки вернулись от фоновых потоков: дочитываем непрочитанное и отправляем в видеокарту.
        static void FinishBuild(Build build)
        {
            int byUnity = 0;
            foreach (Slot slot in build.Queue)
            {
                if (slot.Done) continue;
                byUnity++;
                Decode(build, slot);
                // Не прочиталась и так — на её месте должно быть пусто, а не обрывки.
                if (!slot.Done) build.Pages[slot.Page].SetPixels32(slot.X, slot.Y, slot.W, slot.H,
                    new Color32[slot.W * slot.H]);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (Texture2D page in build.Pages) page.Apply(false, true); // в видеопамять, копию в памяти убираем
            build.Next = build.Queue.Count;
            build.Ready = true;
            needScan = true;
            Plugin.Log.LogInfo("Exuvia: " + build.Collection + ": листы готовы за "
                + (build.Clock != null ? build.Clock.ElapsedMilliseconds : 0) + " мс (в фоне; отправка в видеокарту "
                + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " мс"
                    + (byUnity > 0 ? "; дочитано в главном потоке: " + byUnity : "") + ")"
                + (build.Failed > 0 ? ", не прочитано файлов: " + build.Failed : ""));
        }

        /// Забираем готовые листы у фоновых потоков. wait — ждать, пока соберутся все (загрузка сцены).
        static void Pump(bool wait)
        {
            if (jobsOut <= 0) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!wait)
            {
                while (DecodeJobs.PumpOne())
                    if (watch.Elapsed.TotalMilliseconds >= 4.0) break;
            }
            else
            {
                while (jobsOut > 0 && watch.ElapsedMilliseconds < 30000)
                {
                    if (DecodeJobs.PumpOne()) continue;
                    if (DecodeJobs.Outstanding == 0 && !DecodeJobs.HasDone) break;
                    DecodeJobs.WaitDone(20);
                }
            }
            if (jobsOut > 0 && DecodeJobs.Outstanding == 0 && !DecodeJobs.HasDone)
            {
                // Такого быть не должно; чтобы не ждать вечно — собираем эти листы по-старому.
                Plugin.Log.LogWarning("Exuvia: фоновые потоки не вернули листов спрайтов: " + jobsOut
                    + " — собираю в главном потоке.");
                jobsOut = 0;
                foreach (Build build in builds.Values)
                {
                    if (build.JobsOut == 0 || build.Ready) continue;
                    build.JobsOut = 0;
                    foreach (Texture2D page in build.Pages) page.SetPixels32(new Color32[page.width * page.height]);
                    foreach (Slot slot in build.Queue) slot.Done = false;
                    build.Sync = true;
                    build.Next = 0;
                }
                foreach (Build build in retired) build.JobsOut = 0;
            }
        }

        // ---- расшифровка -------------------------------------------------------

        static void Work(double budgetMs)
        {
            int total = 0, done = 0;
            bool busy = false;
            Pump(budgetMs >= 1e6);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            foreach (Build build in builds.Values)
            {
                if (!build.Started) continue;
                total += build.Queue.Count;
                if (build.Ready) { done += build.Queue.Count; continue; }
                if (!build.Sync)
                {
                    // листы собирают фоновые потоки
                    done += Math.Min(build.Queue.Count, build.DoneCount);
                    busy = true;
                    continue;
                }

                while (build.Next < build.Queue.Count && watch.Elapsed.TotalMilliseconds < budgetMs)
                {
                    Decode(build, build.Queue[build.Next]);
                    build.Next++;
                }
                done += build.Next;
                if (build.Next >= build.Queue.Count)
                {
                    // отправляем в видеопамять, копию в памяти убираем
                    foreach (Texture2D page in build.Pages) page.Apply(false, true);
                    build.Ready = true;
                    needScan = true;
                    Plugin.Log.LogInfo("Exuvia: " + build.Collection + ": листы готовы"
                        + (build.Failed > 0 ? ", не прочитано файлов: " + build.Failed : ""));
                }
                else busy = true;
            }
            Busy = busy;
            DoneSprites = done;
            TotalSprites = total;
        }

        static void Decode(Build build, Slot slot)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(slot.File.Path);
                if (scratch == null)
                {
                    scratch = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    scratch.hideFlags = HideFlags.HideAndDontSave;
                }
                if (!ImageConversion.LoadImage(scratch, bytes, false) || scratch.width != slot.W
                    || scratch.height != slot.H)
                {
                    build.Failed++;
                    Plugin.Log.LogWarning("Exuvia: не читается " + slot.File.Path);
                    return;
                }
                build.Pages[slot.Page].SetPixels32(slot.X, slot.Y, slot.W, slot.H, scratch.GetPixels32());
                slot.Done = true;
            }
            catch (Exception e)
            {
                build.Failed++;
                Plugin.Log.LogWarning("Exuvia: " + slot.File.Path + ": " + e.Message);
            }
        }
    }
}
