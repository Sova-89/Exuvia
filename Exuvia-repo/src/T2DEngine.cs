// ============================================================================
//  T2DEngine.cs — подмена обычных спрайтов Unity (меню, инвентарь, фоны, эффекты)
//  картинками из Sprites\T2D паков формата Stitchwork.
//
//  Как работает:
//   • У каждого SpriteRenderer и UI Image смотрим текущий спрайт. Если для него в паке есть
//     картинка (по имени атласа и спрайта, правила как у Stitchwork) — создаём свой спрайт
//     того же размера и с той же точкой опоры и ставим его вместо родного.
//   • Родной спрайт запоминается: при выключении пака всё возвращается сразу, без перезапуска.
//     Текстуры игры не трогаем.
//   • Объекты находим так: перехват присвоения спрайта (сразу) и обход сцены раз в пару секунд
//     (понемногу за кадр).
//   • Аниматор игры каждый кадр сам ставит родной спрайт обратно, поэтому за анимированными
//     объектами надо следить. Чтобы не проверять каждый кадр все тысячи таких объектов, они
//     поделены на две группы:
//       «каждый кадр»  — объекты, у которых уже стоит наша картинка, и анимированные объекты,
//                         которые сейчас показывают текстуру, для которой в паке есть картинки;
//       «по очереди»   — остальные анимированные: каждого проверяем примерно раз в ColdPeriod секунд,
//                         и как только он показал подходящую текстуру — переводим в первую группу.
//   • Проверять каждый кадр даже одну группу дорого, когда в ней тысячи объектов. Поэтому мод
//     просит саму игру сообщать о смене картинки: у SpriteRenderer для этого есть штатное
//     уведомление (RegisterSpriteChangeCallback), а у картинок интерфейса (Image) аниматор после
//     каждого своего шага вызывает OnDidApplyAnimationProperties — его мы перехватываем.
//     Третий случай — объект выключили и включили снова (игра так переиспользует эффекты): при
//     выключении аниматор молча возвращает объекту его исходную картинку. Чтобы узнать о включении,
//     на такие объекты вешается крошечный компонент T2DWake (в конце файла) — он сообщает нам,
//     что объект включился, и мы сразу ставим свою картинку.
//     Доверяем уведомлениям не сразу: пока идёт проверка каждый кадр, мод сверяет, что каждая
//     замеченная смена картинки сопровождалась уведомлением. Набралось TrustAfter подтверждений
//     без единого пропуска — проверка каждый кадр сменяется редкой страховочной (раз в SafetyFrames
//     кадров на объект). Страховка заметила пропуск — возвращаемся к проверке каждый кадр насовсем.
//     Спрайты и картинки интерфейса проверяются и переключаются отдельно.
//  Что менять: частота обхода — ScanEvery, размер порции за кадр — SlicePerFrame,
//  как часто проверять группу «по очереди» — ColdPeriod, строгость проверки уведомлений —
//  TrustAfter и SafetyFrames.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Exuvia
{
    internal static partial class T2DEngine
    {
        const float ScanEvery = 1.5f;     // как часто заново обходить сцену, секунд
        const float CondEvery = 0.5f;     // как часто проверять условия паков, секунд
        const int SlicePerFrame = 400;    // сколько объектов из обхода проверять за кадр
        const float ColdPeriod = 0.1f;    // за сколько секунд обходить группу «по очереди» целиком
        // столько смен картинки подряд должны прийти с уведомлением, чтобы ему поверить
        const int TrustAfter = 150;
        // когда уведомлениям верим: страховочная проверка объекта раз в столько кадров
        const int SafetyFrames = 60;
        const float SafetyColdPeriod = 1f; // и обход группы «по очереди» за столько секунд

        // Что Handle узнал про объект.
        const int None = 0;      // картинок для его текстуры в паках нет
        const int Touched = 1;   // для этого спрайта замены нет, но для его текстуры картинки в паке есть
        const int Replaced = 2;  // у объекта стоит (или только что поставлена) наша картинка

        sealed class Rep
        {
            internal Sprite Vanilla;
            internal Sprite Ours; // null — замены нет
        }

        sealed class Holder
        {
            internal SpriteRenderer Sr;
            internal Image Img;
            internal int Id;
            internal bool Hot;     // в группе «каждый кадр»
            internal bool IsImg;   // картинка интерфейса (Image), а не SpriteRenderer
            // Сверка уведомлений: какой спрайт видели при прошлой проверке и сколько уведомлений пришло.
            internal int LastSprite;
            internal bool Polled;
            internal int Events, EventsAtPoll;
            // «Будильник» T2DWake: Wake — он стоит на объекте; Asleep — объект сейчас выключен, проверять его незачем.
            internal bool Wake, Asleep;
            internal bool Dead { get { return Sr == null && Img == null; } }
            internal Sprite Current { get { return Sr != null ? Sr.sprite : (Img != null ? Img.sprite : null); } }
            internal void Set(Sprite s) { if (Sr != null) Sr.sprite = s; else if (Img != null) Img.sprite = s; }
        }

        sealed class Tex
        {
            internal Texture2D T;
            internal long Len, Ticks;
            internal bool Failed;
        }

        /// Выключатель из настроек: false — картинки T2D не трогаем совсем.
        internal static bool Enabled = true;

        static T2DWanted wanted = new T2DWanted();
        static string state = "";
        static readonly Dictionary<int, Rep> byVanilla = new Dictionary<int, Rep>();
        static readonly Dictionary<int, Sprite> vanillaOfOurs = new Dictionary<int, Sprite>();
        static readonly Dictionary<string, Tex> textures =
            new Dictionary<string, Tex>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, Holder> hot = new Dictionary<int, Holder>();
        static readonly List<Holder> hotList = new List<Holder>();
        static bool hotChanged;
        static readonly HashSet<int> seen = new HashSet<int>();
        static readonly List<Sprite> retired = new List<Sprite>();
        static readonly List<Holder> retry = new List<Holder>();
        static readonly Rep NoRep = new Rep();          // замены нет, текстура паку не интересна
        static readonly Rep NoRepTouched = new Rep();   // замены нет, но для этой текстуры в паке есть картинки

        // Группа «по очереди»: анимированные объекты, которые пока не показывали ничего подходящего.
        static readonly List<Holder> cold = new List<Holder>();
        static readonly Dictionary<int, Holder> coldById = new Dictionary<int, Holder>();
        static int coldAt;

        // Уведомления игры о смене картинки (см. шапку файла). Индекс 0 — SpriteRenderer, 1 — Image.
        static readonly HashSet<int> listening = new HashSet<int>(); // у каких SpriteRenderer стоит наше уведомление
        static UnityEngine.Events.UnityAction<SpriteRenderer> onSpriteChanged;
        static readonly int[] seenChanges = new int[2];   // смена картинки пришла с уведомлением
        static readonly int[] missedChanges = new int[2]; // смена картинки без уведомления (у включённого объекта)
        // то же у выключенного объекта — это не страшно, его разбудит T2DWake
        static readonly int[] quietChanges = new int[2];
        static int missLogged;
        static int wakeFailures; // объект оказался включён, а будильник об этом не сообщил
        static int awakeHot;     // сколько объектов группы «каждый кадр» реально проверили в прошлом кадре
        static readonly bool[] trusted = new bool[2];     // уведомлениям верим, каждый кадр не проверяем
        static readonly bool[] refused = new bool[2];     // уведомления подвели — больше не верим
        static int sets;        // сколько раз Handle поставил нашу картинку
        static int finalSprite; // какой спрайт остался на объекте после Handle
        static int coldPass;

        static SpriteRenderer[] sliceSr;
        static Image[] sliceImg;
        static int sliceAt;

        static bool busy, patched, needScan = true, sceneJustLoaded;
        static float nextScan, nextCond;
        static System.Diagnostics.Stopwatch clock;
        static double budget;
        static int failedSets;
        static int loggedCount = -1;

        /// Сколько своих спрайтов создано и сколько МБ занимают их картинки.
        internal static int SpriteCount { get { return vanillaOfOurs.Count; } }

        internal static double Megabytes
        {
            get
            {
                double px = 0;
                foreach (Tex tex in textures.Values) if (tex.T != null) px += (double)tex.T.width * tex.T.height;
                return px * 4.0 / (1024.0 * 1024.0);
            }
        }

        internal static void Request()
        {
            needScan = true;
            nextCond = 0f;
        }

        /// Сцена загрузилась: старые объекты исчезли, списки можно почистить.
        internal static void OnSceneLoaded()
        {
            sceneJustLoaded = true;
            Request();
        }

        /// Вызывается каждый кадр из LateUpdate (после аниматоров) и с большим бюджетом на загрузке сцены.
        internal static void Tick(double budgetMs)
        {
            try
            {
                float now = Time.unscaledTime;
                if (now >= nextCond)
                {
                    nextCond = now + CondEvery;
                    RefreshWanted();
                }
                if (wanted.Count == 0 && vanillaOfOurs.Count == 0 && retired.Count == 0) return;
                if (!patched && wanted.Count > 0) Patch(); // перехват ставим, только когда есть что подменять

                clock = System.Diagnostics.Stopwatch.StartNew();
                budget = budgetMs;

                if (sceneJustLoaded)
                {
                    sceneJustLoaded = false;
                    CleanAfterSceneLoad();
                }

                if (retry.Count > 0)
                {
                    var again = new List<Holder>(retry);
                    retry.Clear();
                    foreach (Holder holder in again) if (!holder.Dead) Handle(holder.Sr, holder.Img, holder.Current);
                }

                if (wanted.Count > 0 && (needScan || now >= nextScan))
                {
                    needScan = false;
                    nextScan = now + ScanEvery;
                    sliceSr = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include,
                        FindObjectsSortMode.None);
                    sliceImg = UnityEngine.Object.FindObjectsByType<Image>(FindObjectsInactive.Include,
                        FindObjectsSortMode.None);
                    sliceAt = 0;
                }
                Slice(budgetMs >= 1e6 ? int.MaxValue : SlicePerFrame);
                Enforce();
                PollCold();
                Judge();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при подмене картинок T2D: " + e);
            }
            finally
            {
                clock = null;
            }
        }

        // ---- что подменять -----------------------------------------------------

        static void RefreshWanted()
        {
            CondCtx ctx = SpriteEngine.MakeCtx();
            string now = Enabled ? StitchStore.State(ctx) : "off";
            if (now == state) return;
            state = now;

            RestoreAll();
            wanted = Enabled ? StitchStore.BuildWantedT2D(ctx) : new T2DWanted();

            // Картинки, которых больше нет во включённых паках, убираем из памяти.
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (T2DFile file in wanted.ByKey.Values) keep.Add(file.Path);
            List<string> drop = null;
            foreach (string path in textures.Keys)
            {
                if (keep.Contains(path)) continue;
                if (drop == null) drop = new List<string>();
                drop.Add(path);
            }
            if (drop != null)
                foreach (string path in drop)
                {
                    Tex tex = textures[path];
                    if (tex.T != null) UnityEngine.Object.Destroy(tex.T);
                    textures.Remove(path);
                }

            needScan = true;
            if (wanted.Count > 0) Plugin.Log.LogInfo("Exuvia: картинок T2D в действующих паках: " + wanted.Count);
        }

        /// Возвращаем всем объектам родные спрайты (пак выключили или сменились условия).
        static void RestoreAll()
        {
            int restored = 0;
            busy = true;
            try
            {
                foreach (Holder holder in hot.Values)
                {
                    if (holder.Dead) continue;
                    Sprite cur = holder.Current;
                    if (cur == null) continue;
                    Sprite vanilla;
                    if (!vanillaOfOurs.TryGetValue(cur.GetInstanceID(), out vanilla)) continue;
                    if (vanilla != null)
                    {
                        holder.Set(vanilla);
                        restored++;
                    }
                }
                // Картинки меняли мы сами: сверку уведомлений у этих объектов начинаем заново.
                foreach (Holder holder in hot.Values) holder.Polled = false;
            }
            finally
            {
                busy = false;
            }
            // Свои спрайты уничтожаем позже, на загрузке сцены: вдруг игра ещё держит ссылку на какой-то из них.
            foreach (Rep rep in byVanilla.Values) if (rep.Ours != null) retired.Add(rep.Ours);
            byVanilla.Clear();
            vanillaOfOurs.Clear();
            retry.Clear();
            if (restored > 0) Plugin.Log.LogInfo("Exuvia: T2D: возвращено родных спрайтов: " + restored);
        }

        static void CleanAfterSceneLoad()
        {
            foreach (Sprite sprite in retired) if (sprite != null) UnityEngine.Object.Destroy(sprite);
            retired.Clear();
            seen.Clear();

            List<int> dead = null;
            foreach (KeyValuePair<int, Holder> pair in hot)
            {
                if (!pair.Value.Dead) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pair.Key);
            }
            if (dead != null)
            {
                foreach (int id in dead) hot.Remove(id);
                hotChanged = true;
            }
            for (int i = cold.Count - 1; i >= 0; i--)
            {
                Holder holder = cold[i];
                if (!holder.Hot && !holder.Dead) continue;
                if (!holder.Hot) coldById.Remove(holder.Id);
                cold[i] = cold[cold.Count - 1];
                cold.RemoveAt(cold.Count - 1);
            }
            coldAt = 0;

            // Уведомления стоят только на живых объектах: список пересобираем по ним.
            listening.Clear();
            foreach (Holder holder in hot.Values) if (!holder.Dead) listening.Add(holder.Id);
            foreach (Holder holder in cold) if (!holder.Dead) listening.Add(holder.Id);

            // Родные спрайты выгруженной сцены: их замены больше не нужны.
            dead = null;
            foreach (KeyValuePair<int, Rep> pair in byVanilla)
            {
                if (pair.Value.Vanilla != null || IsNone(pair.Value)) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pair.Key);
            }
            if (dead != null)
                foreach (int id in dead)
                {
                    Rep rep = byVanilla[id];
                    if (rep.Ours != null)
                    {
                        vanillaOfOurs.Remove(rep.Ours.GetInstanceID());
                        UnityEngine.Object.Destroy(rep.Ours);
                    }
                    byVanilla.Remove(id);
                }
            if (byVanilla.Count > 60000) // на всякий случай не даём списку расти бесконечно
            {
                dead = new List<int>();
                foreach (KeyValuePair<int, Rep> pair in byVanilla) if (IsNone(pair.Value)) dead.Add(pair.Key);
                foreach (int id in dead) byVanilla.Remove(id);
            }
        }

        static int AwakeCold()
        {
            int n = 0;
            for (int i = 0; i < cold.Count; i++) if (!cold[i].Hot && !cold[i].Asleep) n++;
            return n;
        }

        static bool IsNone(Rep rep)
        {
            return ReferenceEquals(rep, NoRep) || ReferenceEquals(rep, NoRepTouched);
        }
    }

    /// «Будильник» на анимированном объекте игры: сообщает T2DEngine, что объект включили.
    /// Нужен потому, что при выключении объекта аниматор молча возвращает ему исходную картинку,
    /// а при включении первый кадр может показаться раньше, чем игра сообщит о смене картинки.
    internal sealed class T2DWake : MonoBehaviour
    {
        SpriteRenderer sr;
        Image img;
        int id;
        bool looked;

        void OnEnable()
        {
            if (!looked)
            {
                looked = true;
                sr = GetComponent<SpriteRenderer>();
                if (sr == null) img = GetComponent<Image>();
                id = sr != null ? sr.GetInstanceID() : (img != null ? img.GetInstanceID() : 0);
            }
            T2DEngine.Woke(sr, img);
        }

        void OnDisable()
        {
            if (id != 0) T2DEngine.Slept(id);
        }
    }
}
