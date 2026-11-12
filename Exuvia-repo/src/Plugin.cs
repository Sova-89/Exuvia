// ============================================================================
//  Plugin.cs — вход мода: настройки, клавиша меню, подмена скинов при загрузке сцен.
//  Exuvia: удобный переключатель скинов для Silksong. Заменяет Customizer.
//  Что менять: клавиша и язык — в конфиге BepInEx\config\sova.exuvia.cfg
//  (или в ConfigurationManager, F1). Значения по умолчанию — в Awake ниже.
// ============================================================================
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Exuvia
{
    [BepInPlugin(Guid, "Exuvia", Version)]
    [BepInIncompatibility(OldGuid)] // прежняя версия под именем Wardrobe: вместе они мешали бы друг другу
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "sova.exuvia";
        public const string Version = "0.10.1";
        /// До 0.10.0 мод назывался Wardrobe. Его настройки и память переезжают сами (MoveFromOldName).
        const string OldGuid = "sova.wardrobe";
        const string OldName = "Wardrobe";

        /// Сколько миллисекунд кадра можно тратить на расшифровку PNG, когда игра идёт.
        const double TickBudgetMs = 12.0;

        /// Сколько миллисекунд кадра можно тратить на расшифровку картинок T2D.
        const double T2DBudgetMs = 4.0;

        internal static ManualLogSource Log;

        static ConfigEntry<KeyboardShortcut> menuKey;
        static ConfigEntry<string> language;
        internal static ConfigEntry<bool> ExclusiveOnConflict;
        static ConfigEntry<int> maxCacheMb, keepRecentMb;
        static ConfigEntry<bool> backgroundDecode;
        static ConfigEntry<bool> useT2D, useTexts, useSounds, useRecolor;
        static ConfigEntry<bool> menuButtons;
        static ConfigEntry<bool> logFrameLoad;

        int sizesCount = -1, sizesLogged;
        float sizesSaveAt;

        Menu menu;
        System.Action<UnityEngine.UI.Selectable> openFromMenu;
        float again1, again2;

        void Awake()
        {
            Log = Logger;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            MoveFromOldName();
            long tSkins, tPacks, tStitch;

            // F1–F12, Insert, Home/End, PageUp/PageDown и Delete уже заняты другими модами. Клавишу можно сменить в
            // игре (F1).
            menuKey = Config.Bind("Keys", "Menu", new KeyboardShortcut(KeyCode.Backslash),
                "Открыть / закрыть окно скинов. Open / close the skins window.");
            language = Config.Bind("General", "Language", "auto",
                new ConfigDescription("Язык / language", new AcceptableValueList<string>("auto", "ru", "en")));
            ExclusiveOnConflict = Config.Bind("General", "ExclusiveOnConflict", true,
                "Включение пака выключает другие паки, которые заменяют те же текстуры (скин меняется целиком). "
                + "Если выключить, включённые паки накладываются друг на друга по алфавиту, побеждает последний. "
                + "Enabling a pack disables other packs that replace the same textures. Off = packs are layered "
                    + "alphabetically.");

            maxCacheMb = Config.Bind("General", "MaxCacheMB", 1500,
                new ConfigDescription("Сколько МБ памяти можно отдать расшифрованным скинам (0 = без предела). Лишнее "
                    + "выбрасывается и при надобности загружается снова. "
                    + "Memory limit for decoded skin textures, MB (0 = unlimited).", new AcceptableValueRange<int>(0,
                        8192)));

            keepRecentMb = Config.Bind("General", "KeepRecentMB", -1,
                new ConfigDescription("Сколько МБ видеопамяти держать про запас под недавно использованные скины "
                    + "(атласы, картинки меню и интерфейса, листы спрайтов), "
                    + "чтобы включать их снова мгновенно (-1 = само: пятая часть видеопамяти, не больше 1536; на "
                        + "видеокартах слабее 3 ГБ — 0; 0 = не держать). "
                    + "Video memory kept for recently disabled skins so re-enabling is instant, MB (-1 = auto, 0 = "
                        + "off).", new AcceptableValueRange<int>(-1, 8192)));
            backgroundDecode = Config.Bind("General", "BackgroundDecode", true,
                "Расшифровывать картинки скинов в фоновых потоках, чтобы игра не замирала при включении скина. "
                + "Decode skin images on background threads so the game does not freeze when a skin is enabled.");

            menuButtons = Config.Bind("General", "MenuButtons", true,
                "Пункт «Скины» в меню паузы и в главном меню игры. Клавиша из раздела Keys работает независимо от "
                    + "этого. "
                + "A Skins entry in the pause menu and the main menu. The key from the Keys section works either way.");
            logFrameLoad = Config.Bind("General", "LogFrameLoad", false,
                "Раз в 30 секунд писать в журнал, сколько миллисекунд кадра тратит мод и на что (для поиска тормозов; "
                    + "обычно не нужно). "
                + "Log the mod's per-frame cost every 30 seconds.");
            MenuHook.Enabled = menuButtons.Value;
            menuButtons.SettingChanged += (s, e) => MenuHook.Enabled = menuButtons.Value;

            // Части Stitchwork-скинов можно выключать по отдельности (на случай, если какая-то мешает).
            useT2D = Config.Bind("Stitchwork", "Images", true,
                "Картинки T2D из Stitchwork-скинов: меню, инвентарь, фоны, эффекты. Images from Sprites\\T2D: menus, "
                    + "inventory, backgrounds, effects.");
            useTexts = Config.Bind("Stitchwork", "Texts", true,
                "Тексты из Stitchwork-скинов (папка Text). Texts from the Text folder.");
            useSounds = Config.Bind("Stitchwork", "Sounds", true,
                "Звуки из Stitchwork-скинов (папка Sounds). Sounds from the Sounds folder.");
            useRecolor = Config.Bind("Stitchwork", "Recolor", true,
                "Перекраска сцен из Stitchwork-скинов (папка Recolor). Scene recolor rules from the Recolor folder.");
            T2DEngine.Enabled = useT2D.Value;
            TextEngine.Enabled = useTexts.Value;
            SoundEngine.Enabled = useSounds.Value;
            RecolorEngine.Enabled = useRecolor.Value;
            useT2D.SettingChanged += (s, e) => { T2DEngine.Enabled = useT2D.Value; T2DEngine.Request(); };
            useTexts.SettingChanged += (s, e) => { TextEngine.Enabled = useTexts.Value; TextEngine.Request(); };
            useSounds.SettingChanged += (s, e) => { SoundEngine.Enabled = useSounds.Value; SoundEngine.Request(); };
            useRecolor.SettingChanged += (s,
                e) => { RecolorEngine.Enabled = useRecolor.Value; RecolorEngine.Request(); };

            L.Mode = language.Value;
            language.SettingChanged += (s, e) => L.Mode = language.Value;
            Engine.MaxCacheMb = maxCacheMb.Value;
            maxCacheMb.SettingChanged += (s, e) => Engine.MaxCacheMb = maxCacheMb.Value;
            ApplyKeepRecent();
            keepRecentMb.SettingChanged += (s, e) => ApplyKeepRecent();
            Engine.Background = backgroundDecode.Value;
            T2D.Fast = backgroundDecode.Value;
            backgroundDecode.SettingChanged += (s,
                e) => { Engine.Background = backgroundDecode.Value; T2D.Fast = backgroundDecode.Value; };

            PackStore.LogSink = text => Log.LogWarning("Exuvia: " + text);
            PackStore.Root = Path.Combine(Application.dataPath, Path.Combine("Mods", "Customizer"));

            // Папка Skins: кинул туда zip, rar, 7z или папку — мод сам разбирает, и скин остаётся в ней
            // (Skins\<имя>). Старые места (Mods\Customizer, Packs, BepInEx\plugins) читаются как раньше.
            Installer.SkinsDir = Path.Combine(Paths.GameRootPath, "Skins");
            Installer.Say = L.T; // причины «не установилось» — на языке интерфейса
            Installer.CustomizerRoot = Installer.SkinsDir;
            Installer.StitchRoot = Installer.SkinsDir;
            PackStore.ExtraRoot = Installer.SkinsDir;
            Installer.EnsureDir();
            long t0 = clock.ElapsedMilliseconds;
            Installer.LegacyCustomizerRoot = PackStore.Root;
            Installer.LegacyStitchRoot = Path.Combine(Paths.GameRootPath, "Packs");
            InstallNewSkins();
            Installer.BackfillPreviews(); // значки из уже разобранных архивов — скинам, поставленным раньше
            Preview.CacheDir = Path.Combine(Path.Combine(Paths.CachePath, "Exuvia"), "previews");
            tSkins = clock.ElapsedMilliseconds - t0;

            // Размеры картинок, прочитанные в прошлые запуски (чтобы не открывать тысячи файлов заново).
            PngSizes.CacheFile = Path.Combine(Path.Combine(Paths.CachePath, "Exuvia"), "png-sizes.txt");

            t0 = clock.ElapsedMilliseconds;
            PackStore.Scan();
            tPacks = clock.ElapsedMilliseconds - t0;

            // Скины формата Stitchwork: BepInEx\plugins\...\Stitchwork и <папка игры>\Packs\<имя>\Stitchwork.
            StitchStore.LogSink = text => Log.LogWarning("Exuvia: " + text);
            StitchStore.GameRoot = Paths.GameRootPath;
            StitchStore.PluginsRoot = Paths.PluginPath;
            StitchStore.SkinsRoot = Installer.SkinsDir;
            StitchStore.StateFile = Path.Combine(Paths.ConfigPath, "sova.exuvia.stitch.txt");
            t0 = clock.ElapsedMilliseconds;
            StitchStore.Scan();
            tStitch = clock.ElapsedMilliseconds - t0;

            // Меню и интерфейс скинов применяет CustomizerT2D — учим его выключать их без перезапуска игры.
            T2D.Hook();

            SceneManager.sceneLoaded += OnSceneLoaded;
            menu = new Menu();
            // Из меню игры открывается экран в стиле игры; если его собрать не вышло — старое окно.
            SkinScreen.Init(menu, this);
            openFromMenu = delegate(UnityEngine.UI.Selectable button) { if (!SkinScreen.Open(button)) menu.OpenFrom(button); };
            Engine.Request();
            TextEngine.Tick();  // тексты нужны уже в главном меню
            SoundEngine.Tick();

            Log.LogInfo("Exuvia " + Version + ": скины лежат в " + Installer.SkinsDir
                + " (старые папки тоже читаются), скинов Customizer " + PackStore.Packs.Count
                + ", включено " + PackStore.EnabledCount + "; Stitchwork-паков " + StitchStore.Packs.Count
                + ", включено " + StitchStore.EnabledCount + ".");
            foreach (StitchPack pack in StitchStore.Packs)
                foreach (string problem in pack.Problems) Log.LogWarning("Exuvia: " + pack.Name + ": " + problem);

            // Сколько времени запуска игры занял мод и на что оно ушло.
            long total = clock.ElapsedMilliseconds;
            Log.LogInfo("Exuvia: запуск мода занял " + total + " мс (папка Skins " + tSkins + ", скины Customizer "
                + tPacks
                + ", скины Stitchwork " + tStitch + ", остальное " + (total - tSkins - tPacks - tStitch)
                + "); картинок открыто ради размера: " + PngSizes.HeaderReads
                    + ", отдельных запросов к диску про файл: " + FileLister.SingleStats);
            Log.LogInfo("Exuvia: видеопамять " + SystemInfo.graphicsMemorySize + " МБ ("
                + SystemInfo.graphicsDeviceName + "), обычная память " + SystemInfo.systemMemorySize
                + " МБ, ядер процессора "
                + System.Environment.ProcessorCount + "; картинки скинов расшифровываются "
                    + (Engine.Background ? "в фоне, потоков до " + DecodeJobs.MaxThreads : "в главном потоке")
                + "; про запас под недавние скины оставляю до " + Spare.BudgetMb + " МБ.");
        }

        /// Памятку размеров картинок пишем на диск не сразу, а когда новые записи перестали прибывать.
        void SaveSizesSoon()
        {
            if (!PngSizes.Dirty) { sizesCount = -1; return; }
            float now = Time.unscaledTime;
            if (PngSizes.Count != sizesCount)
            {
                sizesCount = PngSizes.Count;
                sizesSaveAt = now + 3f;
                return;
            }
            if (now < sizesSaveAt) return;
            SaveSizes();
        }

        void SaveSizes()
        {
            if (!PngSizes.Dirty) return;
            int reads = PngSizes.HeaderReads;
            string error = PngSizes.Save();
            sizesCount = -1;
            if (error != null) Log.LogWarning("Exuvia: памятка размеров картинок не записалась: " + error);
            else if (reads > sizesLogged)
                Log.LogInfo("Exuvia: размеры картинок запомнены (записей " + PngSizes.Count
                    + ", в этот запуск открыто файлов: " + reads + ").");
            sizesLogged = reads;
        }

        void OnApplicationQuit()
        {
            Engine.Shutdown(); // дождаться фоновых потоков, которые ещё пишут в память текстур
            SaveSizes();
        }

        /// Запас «само»: пятая часть видеопамяти, но не больше 1536 МБ (этого хватает, чтобы помнить
        /// ещё один большой скин кроме включённого) и не больше восьмой части обычной памяти (у встроенных
        /// видеокарт видеопамять — это обычная память). На слабых видеокартах (меньше 3 ГБ) запаса нет:
        /// скины при каждом включении догружаются в фоне, зато мод не занимает лишнего.
        internal static int AutoSpareMb(int vramMb, int ramMb)
        {
            if (vramMb <= 0) return 512;
            if (vramMb < 3000) return 0;
            int mb = Mathf.Min(vramMb / 5, 1536);
            if (ramMb > 0) mb = Mathf.Min(mb, ramMb / 8);
            return Mathf.Max(mb, 0);
        }

        /// Сколько видеопамяти оставлять под недавно выключенные скины (настройка General/KeepRecentMB).
        static void ApplyKeepRecent()
        {
            int mb = keepRecentMb.Value;
            if (mb < 0) mb = AutoSpareMb(SystemInfo.graphicsMemorySize, SystemInfo.systemMemorySize);
            Spare.BudgetMb = mb;
        }

        /// Мод раньше назывался Wardrobe: забираем его настройки, список включённых скинов и память,
        /// чтобы после смены имени ничего не пришлось настраивать заново. Старые файлы остаются на месте.
        void MoveFromOldName()
        {
            try
            {
                string oldCfg = Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                string newCfg = Path.Combine(Paths.ConfigPath, Guid + ".cfg");
                if (File.Exists(oldCfg) && !File.Exists(newCfg))
                {
                    File.Copy(oldCfg, newCfg);
                    Config.Reload();
                    Log.LogInfo("Exuvia: настройки взяты из прежнего " + OldGuid + ".cfg.");
                }
                string oldState = Path.Combine(Paths.ConfigPath, OldGuid + ".stitch.txt");
                string newState = Path.Combine(Paths.ConfigPath, Guid + ".stitch.txt");
                if (File.Exists(oldState) && !File.Exists(newState)) File.Copy(oldState, newState);
                string oldCache = Path.Combine(Paths.CachePath, OldName);
                string newCache = Path.Combine(Paths.CachePath, "Exuvia");
                if (Directory.Exists(oldCache) && !Directory.Exists(newCache)) Directory.Move(oldCache, newCache);
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Exuvia: не удалось забрать настройки прежней версии (" + e.Message + ").");
            }
        }

        /// При запуске игры ставим всё новое из папки Skins.
        static void InstallNewSkins()
        {
            try
            {
                Installer.Scan();
                if (!Installer.HasNew) return;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var installed = new System.Collections.Generic.List<string>();
                var errors = new System.Collections.Generic.List<string>();
                Installer.ProcessAll(installed, errors);
                if (installed.Count > 0)
                    Log.LogInfo("Exuvia: из папки Skins установлено: " + string.Join(", ", installed.ToArray()) + " ("
                        + watch.ElapsedMilliseconds + " мс)");
                foreach (string e in errors) Log.LogWarning("Exuvia: из папки Skins не установлено: " + e);
                if (Installer.SkippedDlls.Count > 0)
                    Log.LogInfo("Exuvia: в архивах скинов были свои моды авторов ("
                        + string.Join(", ", Installer.SkippedDlls.ToArray()) + ") — их не ставлю: это чужой код. "
                        + "Нужны — положите их в BepInEx\\plugins сами (архив лежит в Skins\\_installed).");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Exuvia: не удалось разобрать папку Skins: " + e.Message);
            }
        }

        void Start()
        {
            T2D.Hook(); // на случай, если CustomizerT2D загрузился позже нас
            // К этому моменту все моды уже загружены: если CustomizerT2D нет, меню и интерфейс подменяем сами.
            T2D.EnableOwn();
            // То же с видео-заставками: если CustomizerCinematic нет, подставляем их сами.
            VideoHook.Enable();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SaveSizes();
            if (menu != null) menu.Shutdown();
            SkinScreen.Shutdown();
        }

        /// Старый Customizer подменяет те же текстуры; вдвоём они мешают друг другу.
        internal static bool CustomizerLoaded()
        {
            try { return Chainloader.PluginInfos.ContainsKey("customizer"); }
            catch (System.Exception) { return false; }
        }

        internal static string MenuKeyName { get { return menuKey.Value.ToString(); } }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int readsBefore = PngSizes.HeaderReads;
            T2D.Prune();
            T2D.Rescan();
            VideoHook.OnSceneLoaded();
            // На загрузке сцены экран всё равно чёрный, поэтому здесь можно потратить время:
            // расшифровать то, чего ещё нет в кэше. Дальше всё берётся из памяти.
            Engine.Apply(double.MaxValue);

            // То же для отдельных спрайтов: первый вызов расшифровывает, второй надевает готовое.
            SpriteEngine.Request();
            SpriteEngine.Tick(double.MaxValue);
            SpriteEngine.Tick(0.0);

            // Картинки T2D, звуки, тексты и перекраска: условия паков могли смениться вместе со сценой.
            T2DEngine.OnSceneLoaded();
            T2DEngine.Tick(double.MaxValue);
            TextEngine.Request();
            SoundEngine.Request();
            RecolorEngine.Request();

            // Часть коллекций игра подгружает уже после загрузки сцены.
            float now = Time.unscaledTime;
            again1 = now + 0.6f;
            again2 = now + 2.5f;

            // Сколько загрузка сцены потратила на скины (чтобы видеть, что именно тормозит).
            if (clock.ElapsedMilliseconds >= 200)
                Log.LogInfo("Exuvia: при загрузке сцены " + scene.name + " скины заняли " + clock.ElapsedMilliseconds
                    + " мс (картинок открыто ради размера: " + (PngSizes.HeaderReads - readsBefore) + ").");
        }

        void Update()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (menuKey.Value.IsDown() && !SkinScreen.IsOpen) menu.Toggle();
            menu.Update();
            MenuHook.Tick(openFromMenu);
            SkinScreen.Tick();

            float now = Time.unscaledTime;
            if (again1 > 0f && now >= again1)
            {
                again1 = 0f;
                Engine.Request();
                SpriteEngine.Request();
                T2DEngine.Request();
                RecolorEngine.Request();
                T2D.Rescan();
            }
            if (again2 > 0f && now >= again2)
            {
                again2 = 0f;
                Engine.Request();
                SpriteEngine.Request();
                T2DEngine.Request();
                RecolorEngine.Request();
                T2D.Rescan();
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            Engine.Tick(TickBudgetMs);
            T2D.Tick();
            Preview.Tick();
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            SpriteEngine.Tick(TickBudgetMs);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            TextEngine.Tick();
            SoundEngine.Tick();
            RecolorEngine.Tick();
            SaveSizesSoon();
            ReportSwaps();
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();

            loadMenu += t1 - t0;
            loadAtlas += t2 - t1;
            loadSprites += t3 - t2;
            loadOther += t4 - t3;
            frameTicks = t4 - t0; // LateUpdate добавит своё и закроет кадр
        }

        // ---- замер нагрузки на кадр --------------------------------------------------
        // Сколько миллисекунд каждого кадра мод тратит сам и на что. Раз в ReportEvery секунд — строка в журнал.
        const float ReportEvery = 30f;
        long loadMenu, loadAtlas, loadSprites, loadOther, loadT2D, frameTicks, worstTicks, totalTicks;
        int loadFrames;
        float loadReportAt;

        void CloseFrame(long lateTicks)
        {
            loadT2D += lateTicks;
            long frame = frameTicks + lateTicks;
            frameTicks = 0;
            totalTicks += frame;
            if (frame > worstTicks) worstTicks = frame;
            loadFrames++;

            float now = Time.unscaledTime;
            if (loadReportAt <= 0f) { loadReportAt = now + ReportEvery; return; }
            if (now < loadReportAt) return;

            if (logFrameLoad.Value && loadFrames > 0)
            {
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double n = loadFrames;
                double hookMs = T2DEngine.HookTicks * toMs;
                double avg = totalTicks * toMs / n + hookMs / n;
                Log.LogInfo("Exuvia: нагрузка на кадр за " + ReportEvery.ToString("0") + " с ("
                    + (n / ReportEvery).ToString("0") + " кадров/с): в среднем "
                    + avg.ToString("0.00") + " мс, худший кадр " + (worstTicks * toMs).ToString("0.0")
                        + " мс. По частям, мс на кадр: картинки T2D "
                    + (loadT2D * toMs / n).ToString("0.00") + ", перехват смены картинок "
                        + (hookMs / n).ToString("0.00")
                    + " (" + (T2DEngine.HookCalls / n).ToString("0") + " раз за кадр), спрайты "
                        + (loadSprites * toMs / n).ToString("0.00")
                    + ", атласы " + (loadAtlas * toMs / n).ToString("0.00") + ", меню "
                        + (loadMenu * toMs / n).ToString("0.00")
                    + ", прочее " + (loadOther * toMs / n).ToString("0.00") + ".");
            }
            loadMenu = loadAtlas = loadSprites = loadOther = loadT2D = worstTicks = totalTicks = 0;
            loadFrames = 0;
            T2DEngine.HookCalls = 0;
            T2DEngine.HookTicks = 0;
            loadReportAt = now + ReportEvery;
        }

        int swapsSeen, swapsLogged;
        double swapMsLogged;
        float swapsQuietAt;

        /// Сколько времени ушло на подмену текстур меню и интерфейса (пишем, когда подмены затихли).
        void ReportSwaps()
        {
            if (T2D.SwapCount == swapsLogged) return;
            float now = Time.unscaledTime;
            if (T2D.SwapCount != swapsSeen)
            {
                swapsSeen = T2D.SwapCount;
                swapsQuietAt = now + 2f;
                return;
            }
            if (now < swapsQuietAt) return;
            Log.LogInfo("Exuvia: подмена текстур меню и интерфейса: " + (T2D.SwapCount - swapsLogged) + " шт. за "
                + (T2D.SwapMs - swapMsLogged).ToString("0") + " мс.");
            swapsLogged = T2D.SwapCount;
            swapMsLogged = T2D.SwapMs;
        }

        void LateUpdate()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            menu.KeepCursor();
            // После аниматоров: они каждый кадр возвращают родной спрайт, а мы ставим свой обратно.
            T2DEngine.Tick(T2DBudgetMs);
            CloseFrame(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }

        void OnGUI()
        {
            if (menu.Open) menu.Draw();
            else if (!SkinScreen.IsOpen) MenuHook.DrawOverlay(false);
        }
    }
}
