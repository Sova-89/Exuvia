// ============================================================================
//  SkinScreen.cs — экран «Скины» в стиле самой игры (вместо серого окна).
//
//  Как сделан: это копия экрана паузы игры (узоры сверху и снизу, список кнопок по центру).
//  Родные кнопки из копии убраны, вместо них — строки со скинами и «Назад».
//  Показывает и прячет экран сама игра (UIManager.ShowMenu / HideMenu), поэтому затухание,
//  узоры, звуки и управление стрелками/геймпадом такие же, как в остальных меню.
//
//  Список длинный — на экране видно VisibleRows строк, остальное листается стрелками
//  (упёрся в край — список сдвигается) или колесом мыши.
//  Что делать при нажатии, решает Menu.cs (ItemSubmit и т.д.) — здесь только вид.
//
//  Если экран собрать не удалось (другая версия игры) — Open вернёт false,
//  и откроется старое окно.
//  Что менять: числа в начале класса (сколько строк видно, размеры, цвета).
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Exuvia
{
    internal static partial class SkinScreen
    {
        // ---- настройки вида ------------------------------------------------------
        const int VisibleRows = 6;          // сколько скинов видно сразу
        const float RowHeight = 66f;        // высота строки скина (у родных кнопок паузы 75)
        const float TitleScale = 1.25f;     // размер заголовка относительно строки
        const float InfoScale = 0.62f;      // размер подписи под списком
        const float DotsScale = 0.7f;       // размер «· · ·» (есть ещё строки выше/ниже)
        const float InfoWidth = 1000f, InfoHeight = 96f;
        const float DotsHeight = 22f;
        const float OffAlpha = 0.45f;       // насколько бледнее выключенный скин
        const string OnColor = "#A9F2B4";   // цвет пометки «вкл»
        const float MarkScale = 0.72f;      // размер пометок «вкл» / «не установлено» относительно имени
        const int InfoChars = 135;          // подпись под списком не длиннее двух строк
        const string BadColor = "#FF9C85";  // цвет пометки «не установлено»
        const float StatusSeconds = 8f;     // сколько держится сообщение о сделанном
        const float PreviewSize = 300f;     // сторона картинки-превью справа от списка
        const float PreviewX = 640f, PreviewY = 30f; // её место: от середины экрана вправо и вверх
        const float BusyLimit = 6f;         // переход между экранами дольше этого считаем зависшим

        enum Ctx { None, Pause, Main }

        static Menu menu;
        static MonoBehaviour host;

        static GameObject root;
        static MenuScreen screen;
        static readonly List<PauseMenuButton> rows = new List<PauseMenuButton>();
        static readonly List<Text> rowLabels = new List<Text>();
        static PauseMenuButton backRow;
        static Text title, dotsTop, dotsBottom, info;
        static RawImage preview;
        static readonly Dictionary<Text, string> shown = new Dictionary<Text, string>();
        static Color baseColor = Color.white;

        static Ctx ctx = Ctx.None;
        static bool open, busy, broken, dumped;
        static float busyUntil;
        static Selectable returnTo;
        static MainMenuState stateAtOpen;
        static int offset;
        static int activeRows = -1;
        static float nextRefresh, lostSince, statusUntil;
        static string lastStatus = "";
        static int lastItem = -2;
        static Selectable lastRow;

        static InputHandler pauseHeld;
        static int releaseAtFrame = -1;

        internal static bool IsOpen { get { return open || busy; } }

        internal static void Init(Menu logic, MonoBehaviour coroutineHost)
        {
            menu = logic;
            host = coroutineHost;
        }

        // ---- открыть / закрыть ---------------------------------------------------

        /// Открыть экран с кнопки меню игры. false — не получилось, пусть откроется старое окно.
        internal static bool Open(Selectable from)
        {
            if (broken || menu == null || host == null) return false;
            if (open || busy) return true;
            UIManager ui = MenuHook.Ui();
            if (ui == null) return false;

            Ctx where = Ctx.None;
            if (ui.pauseMenuScreen != null && ui.pauseMenuScreen.gameObject.activeInHierarchy) where = Ctx.Pause;
            else if (ui.mainMenuScreen != null && ui.mainMenuScreen.gameObject.activeInHierarchy) where = Ctx.Main;
            if (where == Ctx.None || ui.IsFadingMenu) return false;

            try
            {
                if (root == null)
                {
                    ClearBuilt(); // прошлый экран мог исчезнуть вместе с меню игры
                    Build(ui);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: экран скинов в стиле игры собрать не удалось (" + e.Message
                    + ") — будет обычное окно.");
                Forget(true);
                broken = true;
                return false;
            }

            ctx = where;
            returnTo = from;
            stateAtOpen = ui.menuState;
            busy = true;
            busyUntil = Time.unscaledTime + BusyLimit;
            host.StartCoroutine(OpenRoutine(ui));
            return true;
        }

        static IEnumerator OpenRoutine(UIManager ui)
        {
            menu.Prepare();
            offset = 0;
            lastItem = -2;
            lastStatus = menu.Status;
            statusUntil = lastStatus.Length > 0 ? Time.unscaledTime + StatusSeconds : 0f;
            Refresh(true);

            if (ctx == Ctx.Pause)
            {
                HoldPause();
                yield return ui.StartCoroutine(ui.HideMenu(ui.pauseMenuScreen, true));
            }
            else
            {
                FadeTitle(ui, false);
                yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(ui.mainMenuScreen, true, false));
            }
            if (root == null) { Forget(false); yield break; }

            SetNavigation();
            screen.defaultHighlight = FirstRow();
            yield return ui.StartCoroutine(ui.ShowMenu(screen));
            if (root == null) { Forget(false); yield break; }

            SetNavigation(); // кнопки только что «проснулись» и могли сбросить переходы
            Refresh(true);
            open = true;
            busy = false;
            lostSince = 0f;
            Highlight(FirstRow());

            if (!dumped)
            {
                dumped = true;
                yield return null;
                if (root != null) MenuHook.Dump("устройство экрана скинов", root.transform, null);
            }
        }

        /// Назад в то меню, откуда пришли.
        static void Back(bool cancelSound)
        {
            if (!open || busy) return;
            UIManager ui = MenuHook.Ui();
            if (ui == null || root == null) { Forget(false); return; }
            if (cancelSound && ui.uiAudioPlayer != null) ui.uiAudioPlayer.PlayCancel();
            open = false;
            busy = true;
            busyUntil = Time.unscaledTime + BusyLimit;
            host.StartCoroutine(BackRoutine(ui));
        }

        static IEnumerator BackRoutine(UIManager ui)
        {
            yield return ui.StartCoroutine(ui.HideMenu(screen, true));

            Selectable back = returnTo;
            if (ctx == Ctx.Pause)
            {
                yield return ui.StartCoroutine(ui.ShowMenu(ui.pauseMenuScreen));
                Highlight(back);
                ReleasePauseSoon();
            }
            else
            {
                ui.UIGoToMainMenu(); // игра сама вернёт заголовок и кнопки
                float until = Time.unscaledTime + 4f;
                while (Time.unscaledTime < until && ui != null && ui.mainMenuScreen != null
                    && !(ui.mainMenuScreen.interactable && ui.mainMenuScreen.alpha > 0.99f)) yield return null;
                for (int i = 0; i < 3; i++) yield return null; // игра ещё выделяет свою кнопку по умолчанию
                Highlight(back);
            }
            returnTo = null;
            ctx = Ctx.None;
            busy = false;
        }

        /// Мгновенно убрать экран, ничего не показывая взамен (игра сама ушла из меню или сменилась сцена).
        static void Forget(bool destroy)
        {
            try
            {
                if (root != null)
                {
                    CanvasGroup group = root.GetComponent<CanvasGroup>();
                    if (group != null) { group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false; }
                    root.SetActive(false);
                    if (destroy) UnityEngine.Object.Destroy(root);
                }
            }
            catch (Exception) { }
            if (destroy || root == null) ClearBuilt();
            open = false;
            busy = false;
            ctx = Ctx.None;
            returnTo = null;
            ReleasePause();
        }

        static void ClearBuilt()
        {
            foreach (PauseMenuButton button in rows) MenuHook.Unregister(button);
            if (!ReferenceEquals(backRow, null)) MenuHook.Unregister(backRow);
            rows.Clear();
            rowLabels.Clear();
            shown.Clear();
            backRow = null;
            title = dotsTop = dotsBottom = info = null;
            preview = null;
            root = null;
            screen = null;
            activeRows = -1;
        }

        /// Переход завис (ошибка посреди затухания): убираем свой экран и возвращаем меню игры.
        static void Recover(string why)
        {
            Plugin.Log.LogWarning("Exuvia: экран скинов: " + why + " — возвращаю меню игры.");
            Ctx was = ctx;
            Forget(false);
            try
            {
                UIManager ui = MenuHook.Ui();
                if (ui == null) return;
                GameManager game = GameManager.SilentInstance;
                if (was == Ctx.Pause && game != null && game.isPaused && ui.pauseMenuScreen != null
                    && !ui.pauseMenuScreen.gameObject.activeInHierarchy)
                    ui.StartCoroutine(ui.ShowMenu(ui.pauseMenuScreen));
                else if (was == Ctx.Main && ui.mainMenuScreen != null
                    && !ui.mainMenuScreen.gameObject.activeInHierarchy)
                    ui.UIGoToMainMenu();
                if (game != null && game.inputHandler != null) game.inputHandler.StartUIInput();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: меню игры вернуть не удалось: " + e.Message);
            }
        }

        // ---- каждый кадр ---------------------------------------------------------

        internal static void Tick()
        {
            try { TickInner(); }
            catch (Exception e) { Recover("ошибка (" + e.Message + ")"); }
        }

        static void TickInner()
        {
            if (releaseAtFrame >= 0 && Time.frameCount >= releaseAtFrame) ReleasePause();
            if (busy)
            {
                if (Time.unscaledTime > busyUntil) Recover("переход между меню завис");
                // Пока экран проявляется, размеры надписей уже должны быть верными.
                else if (root != null && root.activeInHierarchy && Time.unscaledTime >= nextRefresh) Refresh(false);
                return;
            }
            if (!open) return;

            UIManager ui = MenuHook.Ui();
            if (ui == null || root == null) { Forget(false); return; }

            // Игра сама ушла из меню (сняли паузу, сменили экран) — наш экран тоже убираем.
            if (ctx == Ctx.Pause)
            {
                GameManager game = GameManager.SilentInstance;
                if (game == null || !game.isPaused) { Forget(false); return; }
            }
            else if (ui.menuState != stateAtOpen) { Forget(false); return; }

            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0.01f) Scroll(-1, false);
            else if (wheel < -0.01f) Scroll(1, false);

            // Выбор пропал (щёлкнули мышью мимо): Esc всё равно должен работать, а выбор — вернуться.
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            bool ours = selected != null && selected.transform.IsChildOf(root.transform);
            if (!ours)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Back(true); return; }
                if (lostSince <= 0f) lostSince = Time.unscaledTime;
                else if (Time.unscaledTime - lostSince > 0.4f && !ui.IsFadingMenu)
                {
                    lostSince = 0f;
                    Highlight(lastRow != null && lastRow.gameObject.activeInHierarchy ? lastRow : FirstRow());
                }
            }
            else
            {
                lostSince = 0f;
                Selectable now = selected.GetComponent<Selectable>();
                if (now != null) lastRow = now;
            }

            if (Time.unscaledTime >= nextRefresh) Refresh(false);
            UpdateInfo(selected);
        }

        // ---- список --------------------------------------------------------------

        /// Всего строк в списке: скины, а за ними «Выключить все».
        static int Total()
        {
            int n = menu.ItemCount;
            return n > 0 ? n + 1 : 0;
        }

        static void Refresh(bool force)
        {
            nextRefresh = Time.unscaledTime + 0.2f;
            if (root == null) return;

            int total = Total();
            int visible = Math.Min(total, rows.Count);
            offset = Mathf.Clamp(offset, 0, Math.Max(0, total - visible));

            if (visible != activeRows || force)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    bool on = i < visible;
                    if (rows[i].gameObject.activeSelf != on) rows[i].gameObject.SetActive(on);
                }
                activeRows = visible;
                SetNavigation();
            }

            int fontSize = rowLabels.Count > 0 && rowLabels[0] != null ? rowLabels[0].fontSize : 0;
            for (int i = 0; i < visible; i++) SetRow(rowLabels[i], offset + i, fontSize);

            SetAlpha(dotsTop, offset > 0 ? 0.7f : 0f);
            SetAlpha(dotsBottom, offset + visible < total ? 0.7f : 0f);

            // Свои подписи держим в размер к строкам: размер строк игра меняет сама (язык, портативный режим).
            if (fontSize > 0)
            {
                SetFont(title, fontSize * TitleScale);
                SetFont(info, fontSize * InfoScale);
                SetFont(dotsTop, fontSize * DotsScale);
                SetFont(dotsBottom, fontSize * DotsScale);
            }
        }

        static void SetRow(Text label, int item, int fontSize)
        {
            if (label == null) return;
            int small = Mathf.Max(10, Mathf.RoundToInt(fontSize * MarkScale));
            string text;
            float alpha = 1f;
            if (item < menu.ItemCount)
            {
                string name = Clean(menu.ItemName(item));
                if (menu.ItemFailed(item))
                {
                    text = name + "  <size=" + small + "><color=" + BadColor + ">"
                        + L.T("не установлено", "not installed") + "</color></size>";
                    alpha = 0.8f;
                }
                else if (menu.ItemEnabled(item))
                {
                    text = name + "  <size=" + small + "><color=" + OnColor + ">" + L.T("вкл", "on")
                        + "</color></size>";
                }
                else
                {
                    text = name;
                    alpha = OffAlpha;
                }
            }
            else
            {
                text = L.T("Выключить все", "Disable all");
                alpha = menu.AnyEnabled ? 1f : OffAlpha;
            }
            SetText(label, text);
            SetAlpha(label, alpha);
        }

        /// Имя скина для строки меню: без знаков, которые Unity примет за разметку, и не длиннее экрана.
        static string Clean(string name)
        {
            if (name == null) return "";
            name = name.Replace('<', '(').Replace('>', ')').Replace('_', ' ');
            if (name.Length > 38) name = name.Substring(0, 36) + "...";
            return name;
        }

        static void SetText(Text label, string text)
        {
            if (label == null) return;
            string was;
            if (shown.TryGetValue(label, out was) && was == text) return;
            shown[label] = text;
            label.text = text;
            // Игра выравнивает подписи меню по вертикали своим способом — повторяем его после смены текста.
            FixVerticalAlign fix = label.GetComponent<FixVerticalAlign>();
            if (fix != null) fix.AlignAuto();
        }

        static void SetAlpha(Text label, float alpha)
        {
            if (label == null) return;
            Color tint = baseColor;
            tint.a = baseColor.a * alpha;
            if (label.color != tint) label.color = tint;
        }

        static void SetFont(Text label, float size)
        {
            if (label == null) return;
            int rounded = Mathf.Max(8, Mathf.RoundToInt(size));
            if (label.fontSize != rounded) label.fontSize = rounded;
        }

        /// Сдвинуть список на строку. true — сдвинулся.
        static bool Scroll(int delta, bool sound)
        {
            int total = Total();
            int visible = Math.Min(total, rows.Count);
            int next = Mathf.Clamp(offset + delta, 0, Math.Max(0, total - visible));
            if (next == offset) return false;
            offset = next;
            Refresh(false);
            if (sound)
            {
                UIManager ui = MenuHook.Ui();
                if (ui != null && ui.uiAudioPlayer != null) ui.uiAudioPlayer.PlaySelect();
            }
            return true;
        }

        static Selectable FirstRow()
        {
            if (activeRows > 0 && rows.Count > 0) return rows[0];
            return backRow;
        }

        static void Highlight(Selectable target)
        {
            try
            {
                if (target != null && target.gameObject.activeInHierarchy && target.interactable)
                {
                    UIManager.HighlightSelectableNoSound(target);
                    lastRow = target;
                }
            }
            catch (Exception) { }
        }

        /// Переходы стрелками: по кругу через видимые строки и «Назад».
        static void SetNavigation()
        {
            var order = new List<Selectable>();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null && rows[i].gameObject.activeSelf) order.Add(rows[i]);
            if (backRow != null) order.Add(backRow);
            for (int i = 0; i < order.Count; i++)
            {
                var nav = new Navigation();
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnUp = order[(i - 1 + order.Count) % order.Count];
                nav.selectOnDown = order[(i + 1) % order.Count];
                order[i].navigation = nav;
            }
        }

        // ---- нажатия -------------------------------------------------------------

        static void Submit(int row)
        {
            if (!open || busy) return;
            int item = offset + row;
            if (item < menu.ItemCount) menu.ItemSubmit(item);
            else if (item == menu.ItemCount) menu.SubmitDisableAll();
            lastStatus = "";
            Refresh(true);
        }

        /// Стрелка на крайней строке: если дальше есть ещё скины — листаем список, выбор остаётся на месте.
        static bool Move(int row, MoveDirection dir)
        {
            if (!open || busy) return false;
            if (dir == MoveDirection.Up && row == 0) return Scroll(-1, true);
            if (dir == MoveDirection.Down && row == activeRows - 1) return Scroll(1, true);
            return false;
        }

        /// С «Назад» вниз — в начало списка, вверх — в его конец (список по кругу).
        static bool MoveFromBack(MoveDirection dir)
        {
            if (!open || busy) return false;
            int total = Total();
            int visible = Math.Min(total, rows.Count);
            int want = offset;
            if (dir == MoveDirection.Down) want = 0;
            else if (dir == MoveDirection.Up) want = Math.Max(0, total - visible);
            if (want != offset)
            {
                offset = want;
                Refresh(false);
            }
            return false; // сам переход на строку делает игра
        }

        // ---- подпись под списком -------------------------------------------------

        static void UpdateInfo(GameObject selected)
        {
            if (info == null) return;
            int item = -1; // -1 — ничего, -3 — «Назад»
            if (selected != null)
            {
                if (backRow != null && selected == backRow.gameObject) item = -3;
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i] != null && selected == rows[i].gameObject) item = offset + i;
            }
            if (item != lastItem)
            {
                lastItem = item;
                statusUntil = 0f; // перешли на другую строку — сообщение о прошлом действии больше не нужно
            }
            string status = menu.Status;
            if (status != lastStatus)
            {
                lastStatus = status;
                if (status.Length > 0) statusUntil = Time.unscaledTime + StatusSeconds;
            }

            string text = "";
            if (status.Length > 0 && Time.unscaledTime < statusUntil) text = status;
            else if (item >= 0 && item < menu.ItemCount) text = menu.ItemInfo(item);
            else if (item >= 0 && item == menu.ItemCount)
                text = L.T("Вернуть обычный вид игры.", "Back to the vanilla look.");

            if (menu.ItemCount == 0 && text.Length == 0)
                text = L.T("Скинов пока нет. Кинь скин (zip, rar, 7z или папку) в папку Skins в папке игры — он "
                    + "появится здесь.",
                    "No skins yet. Drop a skin (zip, rar, 7z or a folder) into the Skins folder in the game folder — "
                        + "it will show up here.");

            string progress = Menu.Progress();
            if (progress != null) text = text.Length > 0 ? progress + "  " + text : progress;
            if (text.Length > InfoChars) text = text.Substring(0, InfoChars - 3) + "...";
            SetText(info, text);
            UpdatePreview(item);
        }

        /// Картинка выбранного скина справа от списка (нет картинки — место пустое).
        static void UpdatePreview(int item)
        {
            if (preview == null) return;
            Texture tex = item >= 0 && item < menu.ItemCount ? menu.ItemPreview(item) : null;
            if (tex == null)
            {
                if (preview.enabled) preview.enabled = false;
                return;
            }
            if (preview.texture != tex)
            {
                preview.texture = tex;
                // вписываем в квадрат, сохраняя пропорции
                float w = tex.width, h = tex.height;
                float scale = PreviewSize / Mathf.Max(1f, Mathf.Max(w, h));
                preview.rectTransform.sizeDelta = new Vector2(w * scale, h * scale);
            }
            if (!preview.enabled) preview.enabled = true;
        }

        // ---- пауза игры ----------------------------------------------------------

        /// Пока открыт наш экран, клавиша паузы не должна снимать паузу: Esc у нас — «Назад».
        static void HoldPause()
        {
            try
            {
                releaseAtFrame = -1;
                if (pauseHeld != null) return;
                GameManager game = GameManager.SilentInstance;
                InputHandler input = game != null ? game.inputHandler : null;
                if (input != null && input.PauseAllowed)
                {
                    input.PreventPause();
                    pauseHeld = input;
                }
            }
            catch (Exception) { }
        }

        static void ReleasePauseSoon()
        {
            if (pauseHeld != null) releaseAtFrame = Time.frameCount + 3;
        }

        static void ReleasePause()
        {
            releaseAtFrame = -1;
            if (pauseHeld == null) return;
            try { pauseHeld.AllowPause(); } catch (Exception) { }
            pauseHeld = null;
        }

        internal static void Shutdown()
        {
            ReleasePause();
        }

        // ---- заголовок игры в главном меню ---------------------------------------

        /// Прячет логотип и подзаголовок игры так же, как это делает сама игра при входе в «Настройки».
        static void FadeTitle(UIManager ui, bool show)
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                MethodInfo fade = typeof(UIManager).GetMethod(show ? "FadeInSprite" : "FadeOutSprite", Any);
                if (fade != null && ui.gameTitle != null)
                    ui.StartCoroutine((IEnumerator)fade.Invoke(ui, new object[] { ui.gameTitle }));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Exuvia: логотип игры не спрятался: " + e.Message); }
            try
            {
                FieldInfo field = typeof(UIManager).GetField("subtitleFSM", Any);
                object fsm = field != null ? field.GetValue(ui) : null;
                if (fsm as UnityEngine.Object != null)
                {
                    MethodInfo send = fsm.GetType().GetMethod("SendEvent", new[] { typeof(string) });
                    if (send != null) send.Invoke(fsm, new object[] { show ? "FADE IN" : "FADE OUT" });
                }
            }
            catch (Exception) { }
        }
    }
}
