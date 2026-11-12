// ============================================================================
//  DecodeJobs.cs — очередь заданий и фоновые потоки расшифровки.
//  Раньше было частью Png.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Exuvia
{
    /// Одно задание: расшифровать файл Path в память Dst (её хозяин — тот, кто задание создал).
    /// Path == null — расшифровывать нечего (нужна только пустая картинка, см. NeedBlank).
    internal sealed class DecodeJob
    {
        internal string Path;
        /// Заодно приготовить пустую картинку размера W x H (Png.Blank) — она запоминается.
        internal bool NeedBlank;
        internal IntPtr Dst;
        internal long DstLen;
        internal int W, H;
        internal object Tag;
        /// Чьё задание (чтобы отменять только свои) и что сделать в главном потоке, когда оно готово.
        internal object Owner;
        internal Action<DecodeJob> Done;
        /// Срочное: встаёт в очередь впереди обычных (например, листы самой Хорнет — раньше мелочей).
        internal bool Urgent;
        /// Своя работа для фонового потока вместо расшифровки одного файла (например, собрать целый лист).
        internal Action<DecodeJob> Work;

        // Заполняет фоновый поток:
        internal string Problem;   // не null — не получилось (пусть попробует Unity)
        internal double Ms;        // сколько заняла расшифровка
    }

    /// Очередь заданий и фоновые потоки. Потоки появляются по мере надобности и сами
    /// завершаются, если 10 секунд нет работы.
    internal static class DecodeJobs
    {
        static readonly object gate = new object();
        static readonly LinkedList<DecodeJob> queue = new LinkedList<DecodeJob>();
        static readonly Queue<DecodeJob> done = new Queue<DecodeJob>();
        static int urgent; // сколько срочных заданий стоит в начале очереди
        static int running, threads;
        static bool stop;

        /// Сколько потоков можно занять. По умолчанию — ядра минус два (главный поток и отрисовка игры), от 1 до 6.
        internal static int MaxThreads = Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2));

        const int IdleExitMs = 10000;

        internal static void Add(DecodeJob job)
        {
            lock (gate)
            {
                if (stop) { job.Problem = "мод завершает работу"; done.Enqueue(job); return; }
                if (job.Urgent)
                {
                    // после других срочных, но впереди обычных
                    LinkedListNode<DecodeJob> node = queue.First;
                    for (int i = 0; i < urgent && node != null; i++) node = node.Next;
                    if (node == null) queue.AddLast(job); else queue.AddBefore(node, job);
                    urgent++;
                }
                else queue.AddLast(job);
                if (threads < MaxThreads && threads < queue.Count + running)
                {
                    threads++;
                    var thread = new Thread(Work);
                    thread.IsBackground = true;
                    thread.Name = "Exuvia PNG";
                    thread.Start();
                }
                Monitor.PulseAll(gate);
            }
        }

        /// Готовое задание или null. Звать из главного потока.
        internal static DecodeJob TakeDone()
        {
            lock (gate) { return done.Count > 0 ? done.Dequeue() : null; }
        }

        /// Есть готовые задания, которые ещё не забрали.
        internal static bool HasDone
        {
            get { lock (gate) { return done.Count > 0; } }
        }

        /// Сколько заданий ещё не готово (в очереди и в работе).
        internal static int Outstanding
        {
            get { lock (gate) { return queue.Count + running; } }
        }

        /// Берёт одно готовое задание и зовёт его обработчик Done. Только из главного потока.
        /// false — готовых заданий нет.
        internal static bool PumpOne()
        {
            DecodeJob job = TakeDone();
            if (job == null) return false;
            if (job.Done != null) job.Done(job);
            return true;
        }

        /// Убирает из очереди задания, к которым ещё не приступили, и возвращает их.
        /// owner == null — все, иначе только задания этого хозяина.
        internal static List<DecodeJob> DropQueued(object owner)
        {
            lock (gate)
            {
                var dropped = new List<DecodeJob>();
                LinkedListNode<DecodeJob> node = queue.First;
                while (node != null)
                {
                    LinkedListNode<DecodeJob> next = node.Next;
                    if (owner == null || ReferenceEquals(node.Value.Owner, owner))
                    {
                        if (node.Value.Urgent) urgent--;
                        dropped.Add(node.Value);
                        queue.Remove(node);
                    }
                    node = next;
                }
                return dropped;
            }
        }

        /// Ждёт, пока появится готовое задание (не дольше ms). false — готового нет и ждать нечего или время вышло.
        internal static bool WaitDone(int ms)
        {
            lock (gate)
            {
                if (done.Count > 0) return true;
                if (queue.Count + running == 0) return false;
                Monitor.Wait(gate, ms);
                return done.Count > 0;
            }
        }

        /// Перед выходом из игры: новых заданий не брать, дождаться тех, что уже пишут в память.
        internal static void Shutdown(int waitMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            lock (gate)
            {
                stop = true;
                queue.Clear();
                urgent = 0;
                Monitor.PulseAll(gate);
                while (running > 0 && watch.ElapsedMilliseconds < waitMs) Monitor.Wait(gate, 20);
            }
        }

        static void Work()
        {
            for (;;)
            {
                DecodeJob job;
                lock (gate)
                {
                    while (queue.Count == 0)
                    {
                        if (stop || (!Monitor.Wait(gate, IdleExitMs) && queue.Count == 0))
                        {
                            threads--;
                            return;
                        }
                    }
                    job = queue.First.Value;
                    queue.RemoveFirst();
                    if (job.Urgent) urgent--;
                    running++;
                }

                Run(job);

                lock (gate)
                {
                    running--;
                    done.Enqueue(job);
                    Monitor.PulseAll(gate);
                }
            }
        }

        static unsafe void Run(DecodeJob job)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (job.NeedBlank) Png.Blank(job.W, job.H);
                if (job.Work != null) job.Work(job);
                else if (job.Path != null)
                {
                    int len;
                    byte[] f = Png.ReadFile(job.Path, out len);
                    PngHead head = Png.Parse(f, len);
                    if (head.Problem != null) job.Problem = head.Problem;
                    else if (!head.Supported) job.Problem = "необычный PNG";
                    else if (head.Width != job.W || head.Height != job.H)
                        job.Problem = "размер картинки не тот, что ожидался";
                    else job.Problem = Png.Decode(f, head, (byte*)job.Dst, job.DstLen);
                }
            }
            catch (Exception e)
            {
                job.Problem = e.Message;
            }
            job.Ms = watch.Elapsed.TotalMilliseconds;
        }
    }
}
