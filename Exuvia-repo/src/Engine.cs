// ============================================================================
//  Engine.cs — подмена атласов спрайтов (tk2d) на PNG из включённых паков.
//
//  Как работает:
//   • Находим все tk2dSpriteCollectionData и у каждого материала смотрим
//     имя текстуры (atlas0, atlas1…). Если у включённого пака есть файл
//     <коллекция>\<текстура>.png — подставляем его в материал.
//   • Оригинальная текстура запоминается, поэтому пак можно выключить или
//     сменить на лету, без перезапуска игры.
//   • Каждый PNG расшифровывается один раз и лежит в кэше. Customizer делал это
//     заново на каждой загрузке сцены (до 3 секунд лага на меню).
//   • Расшифровка идёт в фоновых потоках (Png.cs) прямо в память будущей текстуры; главному
//     потоку остаётся отправить готовое в видеокарту (Finish). Так игра не замирает на
//     0.1–0.3 с на каждый большой атлас. На загрузке сцены (экран и так чёрный) готовые
//     текстуры ждём сразу, но расшифровываются они параллельно. Если файл необычный или
//     фон не сработал — читает Unity, как раньше (DecodeSync).
//   • Текстуры только что выключенных паков остаются в памяти про запас, чтобы повторное
//     включение было мгновенным; предел запаса общий для всего мода (Spare.cs).
//  Что менять: бюджет времени на кадр — в Plugin.cs (TickBudgetMs); сколько времени кадра
//  можно тратить на отправку готовых текстур в видеокарту — UploadBudgetMs ниже.
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
        // после первой готовой текстуры за кадр берём следующую, только если уложились в это время
        const double UploadBudgetMs = 6.0;
        // сколько времени кадра можно тратить на создание пустых текстур под расшифровку
        const double CreateBudgetMs = 4.0;
        const int WaitAllLimitMs = 30000;    // дольше этого загрузка сцены фоновые потоки не ждёт

        sealed class Rec
        {
            internal Material Mat;
            internal Texture Original;
            internal string OrigName;
            internal string Path;
        }

        sealed class Cached
        {
            internal Texture2D Tex;
            internal long Len;
            internal long Ticks;
            internal bool Failed;
            internal long Used; // отметка Spare.Next(), когда текстуру использовали в последний раз
            internal long Bytes; // сколько занимает в видеопамяти
            internal bool Idle;  // лежит про запас: её пак выключен и она ни на что не надета
        }

        /// Текстура, которую сейчас расшифровывает фоновый поток.
        sealed class Loading
        {
            internal string Path, OrigName;
            internal long Len, Ticks;
            internal Texture2D Tex;
            internal Texture Template;
            internal bool Mips;
            internal int W, H;
            internal bool Started; // текстура создана, задание у фоновых потоков
            // Материалы, которые ждут эту текстуру: получат её сразу, как только она будет готова.
            internal readonly List<Waiter> Waiters = new List<Waiter>();
        }

        sealed class Waiter
        {
            internal Material Mat;
            internal int Id;
            internal string Collection, Orig;
        }

        sealed class Ctx
        {
            internal System.Diagnostics.Stopwatch Sw;
            internal double Budget;
            internal bool OutOfBudget;
            internal bool Pending;
            internal int Applied, Restored, Decoded;
        }

        static readonly Dictionary<int, Rec> recs = new Dictionary<int, Rec>();
        static readonly Dictionary<string, Cached> cache =
            new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        static readonly List<Texture2D> graveyard = new List<Texture2D>();
        static readonly Dictionary<string, Loading> loading =
            new Dictionary<string, Loading>(StringComparer.OrdinalIgnoreCase);

        static readonly Queue<Loading> toStart = new Queue<Loading>(); // ждут, пока им создадут текстуру
        static readonly object jobOwner = new object();
        static bool fresh;            // после последнего полного прохода появились готовые текстуры
        // Замер одной «партии» фоновой загрузки (от первой заявки до последней готовой текстуры).
        static System.Diagnostics.Stopwatch batchClock;
        static int batchCount, batchFallbacks;
        static double batchDecodeMs, batchUploadMs, batchWorstUploadMs, batchCreateMs, batchWorstCreateMs;
        static bool backgroundBroken;

        /// Расшифровывать PNG в фоновых потоках (настройка General/BackgroundDecode).
        internal static bool Background = true;


        /// Сколько текстур сейчас расшифровывается в фоне.
        internal static int LoadingCount { get { return loading.Count; } }

        static bool needApply;
        static int seenVersion = -1;
        static int pass;

        /// Потолок памяти под кэш в МБ (0 = без ограничения). Лишнее выбрасывается, начиная с давно не нужного.
        internal static int MaxCacheMb = 1500;

        /// Часть текстур ещё не расшифрована (не хватило времени кадра).
        internal static bool Pending;

        /// Сколько материалов сейчас показывают текстуру из пака.
        internal static int AppliedCount { get { return recs.Count; } }

        /// Сколько PNG лежит в кэше и примерно сколько это в памяти видеокарты.
        internal static int CachedCount
        {
            get
            {
                int n = 0;
                foreach (Cached cached in cache.Values) if (cached.Tex != null) n++;
                return n;
            }
        }

        internal static double CachedMegabytes
        {
            get
            {
                double bytes = 0;
                foreach (Cached cached in cache.Values)
                    if (cached.Tex != null) bytes += cached.Bytes;
                return bytes / (1024.0 * 1024.0);
            }
        }

        /// Попросить применить паки при ближайшем кадре.
        internal static void Request()
        {
            needApply = true;
        }

        /// Вызывается каждый кадр: доделывает работу, если она осталась.
        internal static void Tick(double budgetMs)
        {
            if (loading.Count > 0)
            {
                Kick(CreateBudgetMs);
                Drain();
            }

            // Готовые текстуры материалы получают сразу (Finish). Полный проход нужен, когда сменился набор
            // скинов или попросили, и ещё один — когда партия догрузилась (уборка и то, что могло проскочить).
            if (needApply || seenVersion != PackStore.Version || (loading.Count == 0 && (fresh || Pending)))
                Apply(budgetMs);
        }

        /// Создаём пустые текстуры для заявок и отдаём их фоновым потокам — понемногу, чтобы не было рывка.
        static void Kick(double budgetMs)
        {
            if (toStart.Count == 0) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (toStart.Count > 0)
            {
                Loading load = toStart.Dequeue();
                Loading current;
                // заявку отменили
                if (!loading.TryGetValue(load.Path, out current) || !ReferenceEquals(current, load)) continue;
                double before = watch.Elapsed.TotalMilliseconds;
                if (!Begin(load))
                {
                    // Фон не сработал: эту и остальные заявки снимаем, ближайший проход прочитает их по-старому.
                    loading.Clear();
                    toStart.Clear();
                    needApply = true;
                    return;
                }
                double took = watch.Elapsed.TotalMilliseconds - before;
                batchCreateMs += took;
                if (took > batchWorstCreateMs) batchWorstCreateMs = took;
                if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
        }

        /// Забираем у фоновых потоков готовое и отправляем в видеокарту: одну текстуру за кадр точно,
        /// следующие — пока укладываемся в UploadBudgetMs.
        static void Drain()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (DecodeJobs.PumpOne())
            {
                if (watch.Elapsed.TotalMilliseconds >= UploadBudgetMs) break;
            }
            // Заданий нет, а текстуры числятся в работе — такого быть не должно; чтобы не ждать вечно, бросаем их.
            if (loading.Count > 0 && toStart.Count == 0 && DecodeJobs.Outstanding == 0 && !DecodeJobs.HasDone)
                Abandon("фоновые потоки не вернули");
            BatchEnd();
        }

        // ---- главный проход ----------------------------------------------------

        /// Один проход по всем загруженным коллекциям. budgetMs — сколько миллисекунд
        /// можно тратить на расшифровку PNG (одну текстуру делаем в любом случае).
        internal static void Apply(double budgetMs)
        {
            needApply = false;
            fresh = false;
            bool changed = seenVersion != PackStore.Version;
            seenVersion = PackStore.Version;
            pass++;

            // Набор включённых скинов сменился: то, к чему фоновые потоки ещё не приступили, может быть уже не нужно.
            if (changed) CancelQueued();

            // Ничего не включено и нечего возвращать — работы нет, игру не трогаем.
            if (PackStore.EnabledCount == 0 && recs.Count == 0 && cache.Count == 0 && loading.Count == 0)
            {
                Pending = false;
                return;
            }

            var ctx = new Ctx();
            ctx.Sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Budget = budgetMs;

            try
            {
                Pass(ctx);

                // Загрузка сцены: экран чёрный, поэтому ждём фоновые потоки здесь же и надеваем всё сразу,
                // чтобы сцена не показалась с родными текстурами.
                if (budgetMs >= 1e6 && loading.Count > 0)
                {
                    WaitAll();
                    ctx.Pending = false;
                    Pass(ctx);
                }

                PurgeDead();
                if (!ctx.Pending) Trim();
                FlushGraveyard();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при применении скинов: " + e);
            }

            Pending = ctx.Pending;
            if (ctx.Applied > 0 || ctx.Restored > 0 || ctx.Decoded > 0)
            {
                Plugin.Log.LogInfo("Exuvia: применено " + ctx.Applied + ", возвращено " + ctx.Restored
                    + ", расшифровано PNG " + ctx.Decoded + ", ждут " + (ctx.Pending ? "да" : "нет")
                    + ", " + ctx.Sw.ElapsedMilliseconds + " мс");
            }
        }

        static void Pass(Ctx ctx)
        {
            tk2dSpriteCollectionData[] all = Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>();
            for (int i = 0; i < all.Length; i++)
            {
                tk2dSpriteCollectionData collectionData = all[i];
                if (collectionData == null) continue;
                string collection = collectionData.name;
                Material[] mats = collectionData.materials;
                Process(mats, collection, ctx);

                // После Init() tk2d держит копии материалов; бывает, что это другие объекты.
                Material[] insts = collectionData.materialInsts;
                if (insts != null && !object.ReferenceEquals(insts, mats)) Process(insts, collection, ctx);
            }
        }

        static void Process(Material[] mats, string collection, Ctx ctx)
        {
            if (mats == null) return;
            for (int i = 0; i < mats.Length; i++)
            {
                Material material = mats[i];
                if (material == null) continue;

                int id = material.GetInstanceID();
                Rec rec;
                bool has = recs.TryGetValue(id, out rec);

                string orig = has ? rec.OrigName : MainTexName(material);
                if (string.IsNullOrEmpty(orig)) continue;

                string path = PackStore.Resolve(collection, orig);
                if (path != null)
                {
                    Texture template = has ? rec.Original : material.mainTexture;
                    bool later;
                    Texture2D tex = GetTexture(path, orig, template, ctx, out later);
                    if (later)
                    {
                        ctx.Pending = true;
                        Loading load;
                        if (loading.TryGetValue(path, out load)) AddWaiter(load, material, id, collection, orig);
                    }
                    if (tex == null) continue;
                    if (Put(material, id, orig, path, tex)) ctx.Applied++;
                }
                else if (has)
                {
                    // Этот материал больше не должен подменяться — возвращаем оригинал.
                    if (rec.Original != null) material.mainTexture = rec.Original;
                    recs.Remove(id);
                    ctx.Restored++;
                }
            }
        }

        /// Надевает текстуру пака на материал (родную запоминает). true — материал изменился.
        static bool Put(Material material, int id, string orig, string path, Texture2D tex)
        {
            Rec rec;
            if (!recs.TryGetValue(id, out rec))
            {
                rec = new Rec();
                rec.Mat = material;
                rec.Original = material.mainTexture;
                rec.OrigName = orig;
                recs[id] = rec;
            }
            rec.Path = path;
            if (material.mainTexture == tex) return false;
            material.mainTexture = tex;
            return true;
        }

        static void AddWaiter(Loading load, Material material, int id, string collection, string orig)
        {
            for (int i = 0; i < load.Waiters.Count; i++) if (load.Waiters[i].Id == id) return;
            var waiter = new Waiter();
            waiter.Mat = material;
            waiter.Id = id;
            waiter.Collection = collection;
            waiter.Orig = orig;
            load.Waiters.Add(waiter);
        }

        static string MainTexName(Material material)
        {
            if (!material.HasProperty("_MainTex")) return null;
            Texture texture = material.mainTexture;
            return texture != null ? texture.name : null;
        }

        // ---- расшифровка PNG и кэш --------------------------------------------

        static Texture2D GetTexture(string path, string origName, Texture template, Ctx ctx, out bool later)
        {
            later = false;
            long len, ticks;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                len = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception)
            {
                return null;
            }

            Cached cached;
            Texture2D stale = null; // файл на диске изменили: пока подставляем старую текстуру
            if (cache.TryGetValue(path, out cached))
            {
                if (cached.Len == len && cached.Ticks == ticks)
                {
                    if (cached.Failed) return null;
                    if (cached.Tex != null)
                    {
                        cached.Used = Spare.Next();
                        return cached.Tex;
                    }
                }
                else
                {
                    stale = cached.Tex;
                }
            }

            // Уже расшифровывается в фоне — или отправляем туда сейчас.
            if (loading.ContainsKey(path) || (Background && !backgroundBroken
                && StartLoad(path, len, ticks, origName, template)))
            {
                later = true;
                return stale;
            }

            if (ctx.OutOfBudget)
            {
                later = true;
                return stale;
            }
            return DecodeSync(path, len, ticks, origName, template, stale, ctx);
        }

        /// Старый путь: PNG расшифровывает Unity в главном потоке (для необычных файлов и если фон не сработал).
        static Texture2D DecodeSync(string path, long len, long ticks, string origName, Texture template,
            Texture2D stale, Ctx ctx)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Texture2D texture = null;
            string problem = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    problem = "Unity не смог прочитать PNG";
                    UnityEngine.Object.Destroy(texture);
                    texture = null;
                }
            }
            catch (Exception e)
            {
                problem = e.Message;
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
            }

            var entry = new Cached();
            entry.Len = len;
            entry.Ticks = ticks;
            if (texture == null)
            {
                Plugin.Log.LogWarning("Exuvia: " + path + ": " + problem);
                if (stale != null)
                {
                    // Новый файл не читается — оставляем прежнюю текстуру, пока файл не поправят.
                    entry.Tex = stale;
                    entry.Bytes = SizeOf(stale);
                    cache[path] = entry;
                    return stale;
                }
                entry.Failed = true;
                cache[path] = entry;
                return null;
            }
            if (stale != null) graveyard.Add(stale);

            Dress(texture, origName, template);
            entry.Tex = texture;
            entry.Used = Spare.Next();
            entry.Bytes = SizeOf(texture);
            cache[path] = entry;

            Plugin.Log.LogInfo("Exuvia: " + ShortName(path) + " " + texture.width + "x" + texture.height + ", "
                + watch.ElapsedMilliseconds + " мс (в главном потоке)");

            if (ctx != null)
            {
                ctx.Decoded++;
                if (ctx.Sw.Elapsed.TotalMilliseconds > ctx.Budget) ctx.OutOfBudget = true;
            }
            return texture;
        }

        static void Dress(Texture2D texture, string origName, Texture template)
        {
            texture.name = origName;
            texture.hideFlags = HideFlags.HideAndDontSave; // чтобы игра не выгрузила «неиспользуемую» текстуру
            if (template != null)
            {
                texture.filterMode = template.filterMode;
                texture.wrapMode = template.wrapMode;
                texture.anisoLevel = template.anisoLevel;
            }
        }

        static long SizeOf(Texture2D texture)
        {
            long bytes = (long)texture.width * texture.height * 4;
            return texture.mipmapCount > 1 ? bytes * 4 / 3 : bytes;
        }

        static string ShortName(string path)
        {
            return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) + "\\"
                + System.IO.Path.GetFileName(path);
        }
    }
}
