// ============================================================================
//  SpriteEngine.cs — подмена отдельных спрайтов tk2d из паков формата Stitchwork.
//
//  Как работает:
//   • Для каждой загруженной коллекции (Knight, Tools Cln…), для которой во включённом паке
//     есть файлы Sprites\<коллекция>\<атлас>\<спрайт>.png, собираем свои листы-атласы.
//     Лист собирает фоновый поток (PageWork): чистит память листа и расшифровывает в неё все его
//     картинки (Png.cs); главному потоку остаётся отправить готовый лист в видеокарту. На загрузке
//     сцены листы ждём сразу. Картинки, которые сами не прочитали (необычный PNG), дочитывает Unity.
//     Если фон выключен или не сработал — по-старому: понемногу в главном потоке (бюджет на кадр).
//   • Сборки только что выключенных скинов (и другого креста) остаются про запас (Spare.cs):
//     повторное включение — без расшифровки.
//   • Когда листы коллекции готовы, каждому спрайту с подходящим именем даём новый материал
//     (копия родного, но с нашим листом) и новые uv. Если картинка больше родного спрайта,
//     растёт и его прямоугольник (по якорю или точке опоры из имени файла).
//   • Родные uv, углы и материал запоминаются: выключение пака возвращает всё как было.
//     Родные атласы игры не трогаем вообще.
//  Что менять: бюджет времени — в Plugin.cs (TickBudgetMs); размер листа — MaxPage ниже.
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
        const int MaxPage = 4096;       // наибольшая сторона листа, пикселей
        const int Pad = 2;              // зазор между картинками на листе
        const float ScanEvery = 1.0f;   // как часто искать новые коллекции, секунд
        const float CondEvery = 0.5f;   // как часто проверять условия (крест, сцена), секунд
        const float RefreshEvery = 0.3f; // как часто перерисовывать спрайты, пока листы ещё собираются, секунд

        sealed class Slot
        {
            internal int Page, X, Y, W, H;
            internal SpriteFile File;
            internal bool Done; // картинка лежит на листе (в фоне — пишет фоновый поток, читаем после его окончания)
        }

        /// Задание фоновому потоку: собрать один лист.
        sealed class PageJob
        {
            internal Build B;
            internal int Page, W, H;
            internal IntPtr Ptr;
            internal long Len;
            internal readonly List<Slot> Slots = new List<Slot>();
        }

        sealed class Build
        {
            internal string Collection;
            internal string Signature;
            internal readonly List<Texture2D> Pages = new List<Texture2D>();
            internal readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>();   // "атлас|спрайт"
            // "спрайт", если имя встречается один раз
            internal readonly Dictionary<string, Slot> ByName = new Dictionary<string, Slot>();
            internal readonly List<Slot> Queue = new List<Slot>();
            internal readonly Dictionary<long, Material> Mats = new Dictionary<long, Material>();
            internal int Next;
            internal bool Started, Ready, Disposed;
            internal int Refs;
            internal int Failed;
            internal long Pixels;
            internal string Key;            // коллекция + подпись набора файлов
            internal bool Sync;             // листы собирает главный поток (по-старому)
            internal int JobsOut;           // сколько листов ещё у фоновых потоков
            internal volatile bool Cancel;  // сборка больше не нужна — фоновым потокам бросить работу
            internal int DoneCount;         // сколько картинок уже обработано фоновыми потоками
            internal long Used;             // отметка Spare.Next(), когда сборку отправили в запас
            internal System.Diagnostics.Stopwatch Clock;
        }

        sealed class DefRec
        {
            internal tk2dSpriteDefinition Def;
            internal Vector2[] Uvs;
            internal Vector3[] Positions;
            internal Material MatInst;
            internal Material Ours;
            internal tk2dSpriteDefinition.FlipMode Flipped;
        }

        sealed class Applied
        {
            internal tk2dSpriteCollectionData Coll;
            internal Build Build;
            internal readonly List<DefRec> Recs = new List<DefRec>();
        }

        static readonly Dictionary<int, Applied> applied = new Dictionary<int, Applied>();
        static readonly Dictionary<string, Build> builds =
            new Dictionary<string, Build>(StringComparer.OrdinalIgnoreCase);
        static readonly List<Build> retired = new List<Build>(); // старые сборки, которые ещё надеты на что-то
        // готовые сборки про запас
        static readonly Dictionary<string, Build> spare =
            new Dictionary<string, Build>(StringComparer.OrdinalIgnoreCase);
        static int jobsOut;            // сколько листов сейчас у фоновых потоков (по всем сборкам)
        static bool backgroundBroken;
        static Dictionary<string, Wanted> wanted = new Dictionary<string, Wanted>(StringComparer.OrdinalIgnoreCase);
        static string wantedState = "";
        static readonly HashSet<int> touched = new HashSet<int>(); // коллекции, спрайты которых надо перерисовать

        static bool needScan = true;
        static float nextScan, nextCond, nextRefresh;
        static Texture2D scratch;
        static readonly List<string> lastUnknown = new List<string>();

        internal static string Crest = "";
        internal static string Scene = "";

        /// Идёт ли расшифровка картинок, и сколько сделано.
        internal static bool Busy;
        internal static int DoneSprites, TotalSprites;

        internal static int AppliedSprites
        {
            get
            {
                int n = 0;
                foreach (Applied record in applied.Values) n += record.Recs.Count;
                return n;
            }
        }

        internal static double Megabytes
        {
            get
            {
                long px = 0;
                foreach (Build build in builds.Values) if (build.Started) px += build.Pixels;
                foreach (Build build in retired) px += build.Pixels;
                foreach (Build build in spare.Values) px += build.Pixels;
                return px * 4.0 / (1024.0 * 1024.0);
            }
        }

        internal static void Request()
        {
            needScan = true;
            nextCond = 0f;
        }

        /// Вызывается каждый кадр (и с большим бюджетом — на загрузке сцены).
        internal static void Tick(double budgetMs)
        {
            if (StitchStore.EnabledCount == 0 && applied.Count == 0 && builds.Count == 0 && retired.Count == 0)
            {
                Busy = false;
                return;
            }
            try
            {
                float now = Time.unscaledTime;
                if (now >= nextCond)
                {
                    nextCond = now + CondEvery;
                    if (RefreshWanted()) needScan = true;
                }
                if (needScan || now >= nextScan)
                {
                    needScan = false;
                    nextScan = now + ScanEvery;
                    Scan();
                }
                Work(budgetMs);
                // Перерисовка спрайтов — это обход всех спрайтов сцены (около 10 мс). Пока листы ещё
                // собираются, делаем её не на каждую готовую коллекцию, а не чаще раза в RefreshEvery секунд.
                if (touched.Count > 0 && (!Busy || budgetMs >= 1e6 || now >= nextRefresh))
                {
                    RefreshSprites();
                    nextRefresh = now + RefreshEvery;
                }
                // Старые листы убираем только после перерисовки: до неё спрайты ещё показывают их.
                if (touched.Count == 0) Cleanup();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при подмене спрайтов: " + e);
            }
        }

        // ---- условия -----------------------------------------------------------

        /// Текущее состояние игры для проверки условий паков (крест, сцена, какие паки включены).
        internal static CondCtx MakeCtx()
        {
            var ctx = new CondCtx();
            ReadGameState();
            ctx.Crest = Crest;
            ctx.Scene = Scene;
            ctx.PackActive = delegate (string name)
            {
                foreach (StitchPack pack in StitchStore.Packs)
                    if (pack.Enabled && string.Equals(pack.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            };
            return ctx;
        }

        static bool RefreshWanted()
        {
            CondCtx ctx = MakeCtx();

            string state = StitchStore.State(ctx);
            if (state == wantedState) return false;
            Dictionary<string, Wanted> map = StitchStore.BuildWanted(ctx);
            foreach (string u in ctx.Unknown)
            {
                if (lastUnknown.Contains(u)) continue;
                lastUnknown.Add(u);
                Plugin.Log.LogWarning("Exuvia: не умею проверять " + u + " — считаю, что оно не выполняется.");
            }
            wantedState = state;
            wanted = map;
            return true;
        }

        static void ReadGameState()
        {
            string scene = "";
            string crest = "";
            try
            {
                GameManager game = GameManager.SilentInstance;
                if (game != null)
                {
                    scene = game.sceneName;
                    if (game.playerData != null) crest = game.playerData.CurrentCrestID;
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(scene))
            {
                try { scene = SceneManager.GetActiveScene().name; }
                catch (Exception) { scene = ""; }
            }
            Scene = scene ?? "";
            Crest = crest ?? "";
        }

        // ---- обход коллекций ---------------------------------------------------

        static void Scan()
        {
            // Забываем коллекции, которые игра уже выгрузила.
            List<int> dead = null;
            foreach (KeyValuePair<int, Applied> pair in applied)
            {
                if (pair.Value.Coll != null) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pair.Key);
            }
            if (dead != null)
                foreach (int id in dead)
                {
                    Release(applied[id].Build);
                    applied.Remove(id);
                }

            tk2dSpriteCollectionData[] all = Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>();
            for (int i = 0; i < all.Length; i++)
            {
                tk2dSpriteCollectionData collection = all[i];
                if (collection == null) continue;
                int id = collection.GetInstanceID();
                Applied record;
                applied.TryGetValue(id, out record);

                if (wanted.Count == 0 && record == null) continue;
                // Пока tk2d не подготовил коллекцию, её нельзя трогать: он перезапишет материалы спрайтов.
                if (collection.materialInsts == null || collection.spriteDefinitions == null) continue;

                Wanted want;
                wanted.TryGetValue(collection.name, out want);
                if (want == null)
                {
                    if (record != null) Restore(record, id);
                    continue;
                }

                Build build = GetBuild(want);
                if (!build.Started) Start(build, want);
                if (!build.Ready) continue; // старая сборка (если была) остаётся надетой, пока новая не готова

                if (record != null && record.Build == build)
                {
                    if (!Intact(record)) { Restore(record, id); ApplyTo(collection, id, build); }
                }
                else
                {
                    if (record != null) Restore(record, id);
                    ApplyTo(collection, id, build);
                }
            }

            // Сборки для коллекций, которые больше не нужны.
            List<string> drop = null;
            foreach (KeyValuePair<string, Build> pair in builds)
            {
                if (wanted.ContainsKey(pair.Key)) continue;
                if (drop == null) drop = new List<string>();
                drop.Add(pair.Key);
            }
            if (drop != null)
                foreach (string k in drop)
                {
                    Build build = builds[k];
                    builds.Remove(k);
                    Retire(build);
                }
        }

        static Build GetBuild(Wanted want)
        {
            Build build;
            if (builds.TryGetValue(want.Collection, out build))
            {
                if (build.Signature == want.Signature) return build;
                builds.Remove(want.Collection);
                Retire(build);
            }

            // Такая сборка уже есть: только что снятая (ещё надета на что-то) или лежит про запас.
            string key = want.Collection + "\n" + want.Signature;
            for (int i = 0; i < retired.Count; i++)
            {
                Build sheetBuild = retired[i];
                if (sheetBuild.Key != key || sheetBuild.Cancel || sheetBuild.Disposed || !sheetBuild.Ready) continue;
                retired.RemoveAt(i);
                builds[want.Collection] = sheetBuild;
                return sheetBuild;
            }
            if (spare.TryGetValue(key, out build))
            {
                spare.Remove(key);
                builds[want.Collection] = build;
                Plugin.Log.LogInfo("Exuvia: " + build.Collection + ": листы взяты из запаса (без расшифровки)");
                return build;
            }

            build = new Build();
            build.Collection = want.Collection;
            build.Signature = want.Signature;
            build.Key = key;
            builds[want.Collection] = build;
            return build;
        }

        /// Раскладываем картинки по листам (расшифровка — позже, понемногу).
        static void Start(Build build, Wanted want)
        {
            build.Started = true;
            var keys = new List<string>();
            foreach (KeyValuePair<string, SpriteFile> pair in want.Files)
            {
                // Здесь впервые нужны ширина и высота картинок — и только у этой коллекции.
                if (pair.Value.Readable) keys.Add(pair.Key);
                else Plugin.Log.LogWarning("Exuvia: картинка не читается, пропускаю: " + pair.Value.Path);
            }
            keys.Sort(StringComparer.Ordinal);
            int n = keys.Count;
            var ws = new int[n];
            var hs = new int[n];
            for (int i = 0; i < n; i++)
            {
                SpriteFile file = want.Files[keys[i]];
                ws[i] = file.Width;
                hs[i] = file.Height;
            }

            Packer.Result result = Packer.Pack(ws, hs, MaxPage, Pad);
            foreach (int big in result.TooBig)
                Plugin.Log.LogWarning("Exuvia: слишком большая картинка, пропускаю: " + want.Files[keys[big]].Path);

            var nameCount = new Dictionary<string, int>();
            foreach (Packer.Item item in result.Items)
            {
                SpriteFile file = want.Files[keys[item.Index]];
                var slot = new Slot();
                slot.Page = item.Page;
                slot.X = item.X;
                slot.Y = item.Y;
                slot.W = file.Width;
                slot.H = file.Height;
                slot.File = file;
                build.Slots[keys[item.Index]] = slot;
                build.Queue.Add(slot);

                string nm = file.Name.ToLowerInvariant();
                int cnt;
                nameCount.TryGetValue(nm, out cnt);
                nameCount[nm] = cnt + 1;
                build.ByName[nm] = slot;
            }
            foreach (KeyValuePair<string, int> pair in nameCount) if (pair.Value > 1) build.ByName.Remove(pair.Key);

            // Сначала заполняем по листам: меньше прыжков по памяти.
            build.Queue.Sort(delegate (Slot x, Slot y) { return x.Page != y.Page ? x.Page.CompareTo(y.Page)
                : (x.Y != y.Y ? x.Y.CompareTo(y.Y) : x.X.CompareTo(y.X)); });
            build.Clock = System.Diagnostics.Stopwatch.StartNew();

            // Листы собирают фоновые потоки; не вышло — по-старому, в главном потоке.
            if (!(Engine.Background && !backgroundBroken && StartBackground(build, result)))
            {
                build.Sync = true;
                foreach (Packer.Page page in result.Pages)
                {
                    var texture = new Texture2D(page.W, page.H, TextureFormat.RGBA32, false);
                    texture.name = "Exuvia " + build.Collection + " " + build.Pages.Count;
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.SetPixels32(new Color32[page.W * page.H]); // прозрачный лист
                    build.Pages.Add(texture);
                    build.Pixels += (long)page.W * page.H;
                }
            }

            Plugin.Log.LogInfo("Exuvia: " + build.Collection + ": спрайтов из пака " + build.Queue.Count + ", листов "
                + build.Pages.Count
                + " (~" + (build.Pixels * 4 / (1024 * 1024)) + " МБ)");
        }
    }
}
