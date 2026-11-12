// ============================================================================
//  T2D.Fast.cs — часть класса T2D: быстрая подмена (копии, расшифрованные в фоне, и перенос в текстуры игры).
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
        // ---- быстрая подмена ------------------------------------------------------

        /// Подмена без замирания. true — взяли на себя (текстура получит картинку сразу или как только
        /// та расшифруется); false — не вышло, пусть подменяет CustomizerT2D.
        static bool FastSwap(Saved backup, FileRec rec)
        {
            // эта картинка уже в текстуре
            if (backup.Applied && backup.ShownPath != null
                && string.Equals(backup.ShownPath, rec.Path, StringComparison.OrdinalIgnoreCase)
                && backup.ShownLen == rec.Len && backup.ShownTicks == rec.Ticks) return true;

            if (IsDxt(backup.Tex)) return DxtSwap(backup, rec);

            SkinCopy copy;
            if (copies.TryGetValue(rec.Path, out copy) && copy.Ready && (copy.Len != rec.Len
                || copy.Ticks != rec.Ticks))
            {
                // файл на диске изменили — старая копия не годится
                DropCopy(copy);
                copy = null;
            }
            if (copy == null)
            {
                copy = StartCopy(backup, rec);
                if (copy == null) return false;
            }
            if (!copy.Waiting.Contains(backup)) copy.Waiting.Add(backup);
            if (copy.Ready)
            {
                // Картинка уже готова, а текстура игры (новая, сжатая) ещё нет: пустую картинку для неё
                // готовим в фоне, перенос подождёт её в очереди.
                if (NeedsBlank(backup.Tex, copy.Tex) && !Png.HasBlank(backup.Tex.width, backup.Tex.height))
                {
                    var job = new DecodeJob();
                    job.NeedBlank = true;
                    job.W = backup.Tex.width;
                    job.H = backup.Tex.height;
                    job.Owner = jobOwner;
                    DecodeJobs.Add(job);
                }
                Flush(copy);
            }
            return true;
        }

        static bool IsDxt(Texture2D tex)
        {
            TextureFormat texFormat = tex.format;
            return texFormat == TextureFormat.DXT1 || texFormat == TextureFormat.DXT5
                || texFormat == TextureFormat.DXT1Crunched || texFormat == TextureFormat.DXT5Crunched;
        }

        /// Подмена текстуры вида DXT. Unity, загружая в такую текстуру картинку, сам сжимает её обратно
        /// в DXT, поэтому сделать её несжатой и копировать в неё нашу RGBA-копию нельзя. Первый раз
        /// подменяем по-старому (в главном потоке) и снимаем с результата копию в том же виде; потом —
        /// копирование внутри видеокарты.
        static bool DxtSwap(Saved backup, FileRec rec)
        {
            Texture2D tex = backup.Tex;
            SkinCopy copy;
            if (copies.TryGetValue(rec.Path, out copy))
            {
                if (copy.Ready && copy.Native && copy.Tex != null && copy.Len == rec.Len && copy.Ticks == rec.Ticks
                    && SameKind(tex, copy.Tex))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Graphics.CopyTexture(copy.Tex, tex);
                    backup.Applied = true;
                    backup.ShownPath = copy.Path;
                    backup.ShownLen = copy.Len;
                    backup.ShownTicks = copy.Ticks;
                    copy.Used = Spare.Next();
                    SwapCount++;
                    SwapMs += watch.Elapsed.TotalMilliseconds;
                    return true;
                }
                // что-то ещё расшифровывается под этим именем — пусть подменит CustomizerT2D
                if (!copy.Ready) return false;
                DropCopy(copy);                // устаревшая или не того вида
            }

            OldSwap(backup, rec.Path, rec.Len, rec.Ticks);
            // не подменилась — предупреждение уже в журнале
            if (!backup.Applied || backup.ShownPath == null) return true;
            try
            {
                Texture2D snap = CloneGpu(tex, "Exuvia skin " + tex.name, false);
                var skinCopy = new SkinCopy();
                skinCopy.Path = rec.Path;
                skinCopy.Len = rec.Len;
                skinCopy.Ticks = rec.Ticks;
                skinCopy.Tex = snap;
                skinCopy.Ready = true;
                skinCopy.Native = true;
                skinCopy.Mips = snap.mipmapCount > 1;
                // DXT5 — байт на пиксель
                skinCopy.Bytes = (long)snap.width * snap.height * (snap.mipmapCount > 1 ? 4 : 3) / 3;
                skinCopy.Used = Spare.Next();
                copies[rec.Path] = skinCopy;
            }
            catch (Exception e)
            {
                if (transferWarnings++ < 5)
                    Plugin.Log.LogInfo("Exuvia: копия подменённой текстуры " + tex.name + " не снялась (" + e.Message
                        + ") — она будет подменяться по-старому.");
            }
            return true;
        }

        /// Текстуру игры придётся сначала сделать несжатой, и для этого нужна пустая картинка её размера.
        static bool NeedsBlank(Texture2D tex, Texture2D like)
        {
            return !SameKind(tex, like) && GraphicsFormatUtility.IsCompressedFormat(tex.graphicsFormat);
        }

        /// Создаёт пустую текстуру-копию и отдаёт её память фоновому потоку.
        static unsafe SkinCopy StartCopy(Saved backup, FileRec rec)
        {
            Texture2D texture = null;
            try
            {
                Texture2D tex = backup.Tex;
                bool mips = backup.Copy != null && backup.Copy.mipmapCount > 1;
                TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
                if (mips) flags |= TextureCreationFlags.MipChain;
                texture = new Texture2D(tex.width, tex.height, tex.isDataSRGB ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm, flags);
                texture.hideFlags = HideFlags.HideAndDontSave;
                texture.name = "Exuvia skin " + tex.name;

                NativeArray<byte> raw = texture.GetRawTextureData<byte>();
                long need = (long)tex.width * tex.height * 4;
                if (raw.Length < need) throw new InvalidOperationException("память текстуры меньше ожидаемой");

                var copy = new SkinCopy();
                copy.Path = rec.Path;
                copy.Len = rec.Len;
                copy.Ticks = rec.Ticks;
                copy.Tex = texture;
                copy.Mips = mips;
                copy.Bytes = mips ? need * 4 / 3 : need;

                var job = new DecodeJob();
                job.Path = rec.Path;
                job.Dst = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(raw);
                job.DstLen = need;
                job.W = tex.width;
                job.H = tex.height;
                job.Tag = copy;
                job.Owner = jobOwner;
                job.Done = CopyDone;
                job.NeedBlank = NeedsBlank(tex, texture); // заодно пустую картинку, если текстура игры сжатая

                if (batchClock == null)
                {
                    batchClock = System.Diagnostics.Stopwatch.StartNew();
                    batchDecoded = batchMoved = batchOld = batchPlain = 0;
                    batchDecodeMs = batchUploadMs = batchMoveMs = batchWorstMs = batchPlainMs = 0;
                }
                copies[rec.Path] = copy;
                copiesLoading++;
                DecodeJobs.Add(job);
                return copy;
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                fastBroken = true;
                Plugin.Log.LogWarning("Exuvia: быстрая подмена меню и интерфейса не заработала (" + e.Message
                    + ") — подменяю по-старому.");
                return null;
            }
        }

        /// Фоновый поток расшифровал картинку (зовётся в главном потоке).
        static void CopyDone(DecodeJob job)
        {
            SkinCopy copy = job.Tag as SkinCopy;
            if (copy == null) return;
            copiesLoading = Math.Max(0, copiesLoading - 1);
            SkinCopy current;
            if (!copies.TryGetValue(copy.Path, out current) || !ReferenceEquals(current, copy))
            {
                if (copy.Tex != null) UnityEngine.Object.Destroy(copy.Tex);
                return;
            }

            if (job.Problem != null)
            {
                // Сами расшифровать не смогли (необычный файл) — подменяем по-старому, как делал CustomizerT2D.
                copies.Remove(copy.Path);
                if (copy.Tex != null) UnityEngine.Object.Destroy(copy.Tex);
                Plugin.Log.LogInfo("Exuvia: " + System.IO.Path.GetFileName(copy.Path) + ": в фоне не вышло ("
                    + job.Problem + "), подменяю в главном потоке.");
                foreach (Saved backup in copy.Waiting)
                    if (Wanted(backup, copy.Path)) OldSwap(backup, copy.Path, copy.Len, copy.Ticks);
                copy.Waiting.Clear();
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                copy.Tex.Apply(copy.Mips, true); // в видеокарту
            }
            catch (Exception e)
            {
                copies.Remove(copy.Path);
                UnityEngine.Object.Destroy(copy.Tex);
                Plugin.Log.LogWarning("Exuvia: " + System.IO.Path.GetFileName(copy.Path)
                    + ": картинка не отправилась в видеокарту (" + e.Message + "), подменяю по-старому.");
                foreach (Saved backup in copy.Waiting)
                    if (Wanted(backup, copy.Path)) OldSwap(backup, copy.Path, copy.Len, copy.Ticks);
                copy.Waiting.Clear();
                return;
            }
            copy.Ready = true;
            copy.Used = Spare.Next();
            batchDecoded++;
            batchDecodeMs += job.Ms;
            double ms = watch.Elapsed.TotalMilliseconds;
            batchUploadMs += ms;
            if (ms > batchWorstMs) batchWorstMs = ms;
            Flush(copy);
        }

        static void Flush(SkinCopy copy)
        {
            foreach (Saved backup in copy.Waiting) transfers.Enqueue(new KeyValuePair<SkinCopy, Saved>(copy, backup));
            copy.Waiting.Clear();
        }

        /// Текстуре всё ещё нужна картинка именно из этого файла (скин не выключили и не сменили)?
        static bool Wanted(Saved backup, string path)
        {
            string now;
            return backup.Tex != null && lazyPaths != null && lazyPaths.TryGetValue(backup.Key, out now)
                && string.Equals(now, path, StringComparison.OrdinalIgnoreCase);
        }

        static bool SameKind(Texture2D a, Texture2D b)
        {
            return a.width == b.width && a.height == b.height && a.graphicsFormat == b.graphicsFormat
                && a.mipmapCount == b.mipmapCount;
        }

        /// Переносит готовую картинку в текстуру игры (внутри видеокарты).
        /// false — пока рано (ждём пустую картинку от фоновых потоков), повторить позже.
        static bool Transfer(SkinCopy copy, Saved backup)
        {
            if (copy.Tex == null || !Wanted(backup, copy.Path)) return true;
            Texture2D tex = backup.Tex;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!SameKind(tex, copy.Tex))
                {
                    if (GraphicsFormatUtility.IsCompressedFormat(tex.graphicsFormat))
                    {
                        // Сжатую текстуру превращаем в несжатую загрузкой картинки: для нечитаемых это
                        // единственный способ, а читаемым сжатым (DXT5) задать новый вид напрямую Unity не дал.
                        // Загружаем пустую того же размера — текстура становится обычной несжатой
                        // (такой же её делала подмена у CustomizerT2D), дальше в неё можно копировать.
                        if (!Png.HasBlank(tex.width, tex.height) && blankWaits < BlankWaitFrames)
                        {
                            blankWaits++;
                            return false;
                        }
                        if (!ImageConversion.LoadImage(tex, Png.Blank(tex.width, tex.height), false))
                            throw new Exception("текстура не приняла пустую картинку");
                        double plainMs = watch.Elapsed.TotalMilliseconds;
                        batchPlain++;
                        batchPlainMs += plainMs;
                    }
                    else if (tex.isReadable)
                    {
                        // Несжатой читаемой текстуре вид можно задать напрямую.
                        if (tex.Reinitialize(copy.Tex.width, copy.Tex.height, copy.Tex.graphicsFormat,
                            copy.Tex.mipmapCount > 1)) tex.Apply(false, false);
                    }
                    // Нечитаемая, но уже несжатая (её подменяли раньше) — в неё можно копировать как есть.
                }
                if (SameKind(tex, copy.Tex)) Graphics.CopyTexture(copy.Tex, tex);
                else if (tex.width != copy.Tex.width || tex.height != copy.Tex.height
                    || !Graphics.ConvertTexture(copy.Tex, tex))
                    throw new Exception("видеокарта не смогла скопировать (текстура " + tex.width + "x" + tex.height
                        + " " + tex.graphicsFormat + ")");

                backup.Applied = true;
                backup.ShownPath = copy.Path;
                backup.ShownLen = copy.Len;
                backup.ShownTicks = copy.Ticks;
                copy.Used = Spare.Next();
                double ms = watch.Elapsed.TotalMilliseconds;
                SwapCount++;
                SwapMs += ms;
                batchMoved++;
                batchMoveMs += ms;
                if (ms > batchWorstMs) batchWorstMs = ms;
            }
            catch (Exception e)
            {
                if (transferWarnings++ < 5)
                    Plugin.Log.LogWarning("Exuvia: быстрая подмена текстуры " + tex.name + " не вышла (" + e.Message
                        + ") — подменяю по-старому.");
                OldSwap(backup, copy.Path, copy.Len, copy.Ticks);
            }
            return true;
        }

        /// Подмена как у CustomizerT2D: картинку расшифровывает Unity в главном потоке прямо в текстуру игры.
        static void OldSwap(Saved backup, string path, long len, long ticks)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (backup.Tex == null) return;
                if (!ImageConversion.LoadImage(backup.Tex, System.IO.File.ReadAllBytes(path), false))
                    throw new Exception("Unity не смог прочитать картинку");
                backup.Applied = true;
                backup.ShownPath = path;
                backup.ShownLen = len;
                backup.ShownTicks = ticks;
                SwapCount++;
                SwapMs += watch.Elapsed.TotalMilliseconds;
                batchOld++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: текстура " + (backup.Tex != null ? backup.Tex.name : "?")
                    + " не подменилась: " + e.Message);
            }
        }

        static void DropCopy(SkinCopy copy)
        {
            SkinCopy current;
            if (copies.TryGetValue(copy.Path, out current) && ReferenceEquals(current, copy)) copies.Remove(copy.Path);
            if (copy.Tex != null) UnityEngine.Object.Destroy(copy.Tex);
        }

        /// Каждый кадр: забираем готовое у фоновых потоков и переносим картинки в текстуры игры — понемногу.
        internal static void Tick()
        {
            if (own && ownKeys.Count > 0)
            {
                try { OwnScanTick(); }
                catch (Exception) { scan = null; }
            }
            if (copiesLoading == 0 && transfers.Count == 0 && batchClock == null) return;

            if (copiesLoading > 0 || transfers.Count > 0)
            {
                var pump = System.Diagnostics.Stopwatch.StartNew();
                while (DecodeJobs.PumpOne())
                    if (pump.Elapsed.TotalMilliseconds >= TransferBudgetMs) break;
                if (copiesLoading > 0 && DecodeJobs.Outstanding == 0 && !DecodeJobs.HasDone)
                {
                    // Такого быть не должно; чтобы не ждать вечно — забываем незаконченные копии.
                    Plugin.Log.LogWarning("Exuvia: фоновые потоки не вернули картинок меню и интерфейса: "
                        + copiesLoading + ".");
                    foreach (SkinCopy copy in new List<SkinCopy>(copies.Values))
                        if (!copy.Ready) copies.Remove(copy.Path);
                    copiesLoading = 0;
                }
            }

            if (transfers.Count > 0)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int tries = transfers.Count;
                while (tries-- > 0 && transfers.Count > 0)
                {
                    KeyValuePair<SkinCopy, Saved> t = transfers.Dequeue();
                    // пустая картинка ещё не готова — в конец очереди
                    if (!Transfer(t.Key, t.Value)) transfers.Enqueue(t);
                    if (watch.Elapsed.TotalMilliseconds >= TransferBudgetMs) break;
                }
                if (transfers.Count == 0) blankWaits = 0;
            }

            if (batchClock != null && copiesLoading == 0 && transfers.Count == 0)
            {
                if (batchDecoded + batchMoved + batchOld > 0)
                    Plugin.Log.LogInfo("Exuvia: меню и интерфейс: в текстуры игры перенесено картинок: " + batchMoved
                        + " за " + batchClock.ElapsedMilliseconds
                        + " мс (расшифровано в фоне: " + batchDecoded + ", суммарно " + batchDecodeMs.ToString("0")
                            + " мс; отправка в видеокарту "
                        + batchUploadMs.ToString("0") + " мс, перенос " + batchMoveMs.ToString("0")
                            + " мс, из него превращение сжатых текстур игры в несжатые: "
                        + batchPlain + " шт., " + batchPlainMs.ToString("0") + " мс; самая долгая заминка "
                            + batchWorstMs.ToString("0.0") + " мс"
                        + (batchOld > 0 ? "; по-старому подменено: " + batchOld : "") + "); готовых картинок в памяти: "
                            + copies.Count + " (~" + (CopiesBytes() / (1024 * 1024)) + " МБ)");
                batchClock = null;
                TrimCopies();
            }
        }

        static long CopiesBytes()
        {
            long bytes = 0;
            foreach (SkinCopy copy in copies.Values) bytes += copy.Bytes;
            return bytes;
        }
    }
}
