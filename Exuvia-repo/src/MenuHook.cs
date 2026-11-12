// ============================================================================
//  MenuHook.cs — пункт «Скины» в меню паузы и в главном меню игры (как у Custom Knight в HK).
//
//  Как работает: берём готовую кнопку игры, делаем её копию, меняем подпись и действие
//  и вставляем перед последним пунктом меню. В меню паузы список кнопок ведёт сама игра
//  (MenuButtonList) — добавляем туда запись, и переходы клавишами настраиваются сами.
//  В главном меню переходы заданы вручную, поэтому их поправляем сами и следим, чтобы
//  игра их не сбросила.
//  Если вставить кнопку не вышло (другая версия игры, чужой мод перестроил меню) —
//  в этом меню показывается запасная кнопка сверху экрана (мышью).
//  Что менять: подпись — Label(); выключатель — настройка General/MenuButtons.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Exuvia
{
    internal static class MenuHook
    {
        const float CheckEvery = 0.5f;
        const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// Выключатель из настроек.
        internal static bool Enabled = true;

        /// Что делает наша кнопка-копия PauseMenuButton: нажатие, отмена (Esc / B) и стрелки.
        internal sealed class RowActions
        {
            internal Action Submit;
            internal Action Cancel;                       // null — как у родной кнопки
            internal Func<MoveDirection, bool> Move;      // вернуть true — «сам обработал, выбор не двигать»
            internal bool DeselectOnSubmit;               // снять выбор при нажатии (когда уходим на другой экран)
        }

        static readonly Dictionary<Selectable, RowActions> rowActions = new Dictionary<Selectable, RowActions>();

        internal static void Register(Selectable button, RowActions actions)
        {
            PatchPause();
            rowActions[button] = actions;
        }

        internal static void Unregister(Selectable button)
        {
            rowActions.Remove(button);
        }

        static Action<Selectable> open;
        static Selectable pauseButton;
        static MenuButton mainButton;
        static bool pausePatched;
        internal const PauseMenuButton.PauseButtonType UnknownType = (PauseMenuButton.PauseButtonType)99;
        static MethodInfo playSubmitSound;
        static bool pauseTried, mainTried;
        static float nextCheck;
        static GUIStyle overlayStyle;

        /// Вызывается каждый кадр. onOpen получает кнопку, с которой открыли окно (чтобы потом вернуть на неё выбор).
        internal static void Tick(Action<Selectable> onOpen)
        {
            open = onOpen;
            if (!Enabled) return;
            float now = Time.unscaledTime;
            if (now < nextCheck) return;
            nextCheck = now + CheckEvery;

            UIManager ui = Ui();
            if (ui == null) return;

            // Игра пересоздала меню (например, после выхода в главное меню) — наши кнопки пропали вместе со старым.
            if (!ReferenceEquals(pauseButton, null) && pauseButton == null)
            {
                rowActions.Remove(pauseButton);
                pauseButton = null;
                pauseTried = false;
            }
            if (!ReferenceEquals(mainButton, null) && mainButton == null)
            {
                mainButton = null;
                mainTried = false;
            }

            if (!pauseTried)
            {
                pauseTried = true;
                try { pauseButton = AddToPause(ui); }
                catch (Exception e) { Plugin.Log.LogWarning("Exuvia: кнопка в меню паузы не добавилась: "
                    + e.Message); }
                if (pauseButton == null) Plugin.Log.LogInfo("Exuvia: в меню паузы будет запасная кнопка «Скины» "
                    + "сверху экрана.");
            }
            if (!mainTried && MainMenuShown(ui))
            {
                mainTried = true;
                try { mainButton = AddToMain(ui); }
                catch (Exception e) { Plugin.Log.LogWarning("Exuvia: кнопка в главном меню не добавилась: "
                    + e.Message); }
                if (mainButton == null) Plugin.Log.LogInfo("Exuvia: в главном меню будет запасная кнопка «Скины» "
                    + "сверху экрана.");
            }
            if (mainButton != null)
            {
                try { LinkMain(ui); }
                catch (Exception) { }
            }
        }

        /// Запасная кнопка в углу экрана — только там, где настоящую вставить не удалось.
        internal static void DrawOverlay(bool windowOpen)
        {
            if (!Enabled || windowOpen || open == null) return;
            UIManager ui = Ui();
            if (ui == null) return;
            bool show = (pauseTried && pauseButton == null && PauseShown(ui)) || (mainTried && mainButton == null
                && MainMenuShown(ui));
            if (!show) return;

            if (overlayStyle == null)
            {
                overlayStyle = new GUIStyle(GUI.skin.button);
                overlayStyle.fontSize = 20;
            }
            float scale = Screen.height / Menu.RefHeight;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float screenW = Screen.width / scale;
            if (GUI.Button(new Rect((screenW - 170f) / 2f, 20f, 170f, 44f), Label(), overlayStyle)) open(null);
            GUI.matrix = old;
        }

        internal static string Label()
        {
            return L.T("Скины", "Skins");
        }

        // ---- доступ к меню игры -------------------------------------------------

        static FieldInfo uiInstance;

        /// UIManager без шума в логе: его собственное свойство instance ругается, если меню ещё не создано.
        internal static UIManager Ui()
        {
            try
            {
                if (uiInstance == null) uiInstance = typeof(UIManager).GetField("_instance",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return uiInstance != null ? uiInstance.GetValue(null) as UIManager : null;
            }
            catch (Exception) { return null; }
        }

        static bool PauseShown(UIManager ui)
        {
            return ui.pauseMenuScreen != null && ui.pauseMenuScreen.gameObject.activeInHierarchy;
        }

        static bool MainMenuShown(UIManager ui)
        {
            return ui.mainMenuScreen != null && ui.mainMenuScreen.gameObject.activeInHierarchy
                && ui.mainMenuScreen.alpha > 0.5f && ui.mainMenuButtons != null;
        }

        // ---- меню паузы ----------------------------------------------------------

        static Selectable AddToPause(UIManager ui)
        {
            MenuScreen screen = ui.pauseMenuScreen;
            if (screen == null) return null;
            MenuButtonList list = screen.GetComponent<MenuButtonList>();
            if (list == null) list = screen.GetComponentInChildren<MenuButtonList>(true);
            if (list == null)
            {
                Plugin.Log.LogInfo("Exuvia: в меню паузы нет списка кнопок (MenuButtonList).");
                return null;
            }

            FieldInfo entriesField = typeof(MenuButtonList).GetField("entries", Priv);
            Type entryType = typeof(MenuButtonList).GetNestedType("Entry",
                BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo selField = entryType != null ? entryType.GetField("selectable", Priv) : null;
            Array entries = entriesField != null ? entriesField.GetValue(list) as Array : null;
            if (entries == null || selField == null || entries.Length == 0)
            {
                Plugin.Log.LogInfo("Exuvia: список кнопок меню паузы пуст или устроен иначе.");
                return null;
            }

            // Кнопки меню по порядку; образец — кнопка перед самой нижней («Настройки»).
            var buttons = new List<Selectable>();
            foreach (object e in entries)
            {
                Selectable selectable = e != null ? selField.GetValue(e) as Selectable : null;
                if (selectable != null) buttons.Add(selectable);
            }
            if (buttons.Count == 0) return null;
            Selectable last = buttons[buttons.Count - 1];
            Selectable donor = null;
            for (int i = buttons.Count - 2; i >= 0 && donor == null; i--)
                if (buttons[i] is PauseMenuButton || buttons[i] is MenuButton) donor = buttons[i];
            if (donor == null && (last is PauseMenuButton || last is MenuButton)) donor = last;
            if (donor == null)
            {
                Plugin.Log.LogInfo("Exuvia: в меню паузы нет кнопки-образца (виды кнопок: " + Kinds(buttons) + ").");
                return null;
            }

            Dump("устройство меню паузы", screen.transform, donor.transform);
            Selectable made = CloneBefore(donor, last, buttons.Count > 1 ? buttons[buttons.Count - 2] : null, buttons);
            if (made == null)
            {
                Plugin.Log.LogInfo("Exuvia: копия кнопки меню паузы не получилась (образец: " + donor.name + ").");
                return null;
            }

            // Запись в список кнопок меню: игра сама настроит переходы вверх и вниз.
            object entry = Activator.CreateInstance(entryType, true);
            selField.SetValue(entry, made);
            CopyEntryExtras(entryType, entries, selField, donor, entry, made);
            Array bigger = Array.CreateInstance(entryType, entries.Length + 1);
            int insertAt = entries.Length - 1;
            for (int i = 0, j = 0; i < bigger.Length; i++)
            {
                if (i == insertAt) bigger.SetValue(entry, i);
                else bigger.SetValue(entries.GetValue(j++), i);
            }
            entriesField.SetValue(list, bigger);
            FieldInfo dirty = typeof(MenuButtonList).GetField("isDirty", Priv);
            if (dirty != null) dirty.SetValue(list, true); // пересоберётся при следующем открытии меню
            try { if (list.gameObject.activeInHierarchy) list.SetupActive(); }
            catch (Exception e) { Plugin.Log.LogWarning("Exuvia: меню паузы не пересобралось сразу: " + e.Message); }

            Plugin.Log.LogInfo("Exuvia: в меню паузы добавлена кнопка «" + Label() + "» (образец: " + donor.name
                + " [" + donor.GetType().Name
                + "], перед: " + last.name + ", раскладка: "
                    + (made.transform.parent.GetComponent<LayoutGroup>() != null ? "сама игра" : "вручную") + ").");
            return made;
        }

        /// Запись образца может говорить «прячь вместе с родителем» или держать свою группу прозрачности — переносим на
        /// копию.
        static void CopyEntryExtras(Type entryType, Array entries, FieldInfo selField, Selectable donor, object entry,
            Selectable made)
        {
            try
            {
                object donorEntry = null;
                foreach (object e in entries)
                    if (e != null && ReferenceEquals(selField.GetValue(e), donor)) donorEntry = e;
                if (donorEntry == null) return;
                FieldInfo parent = entryType.GetField("alsoAffectParent", Priv);
                if (parent != null) parent.SetValue(entry, parent.GetValue(donorEntry));
                FieldInfo group = entryType.GetField("group", Priv);
                if (group != null && group.GetValue(donorEntry) as CanvasGroup != null)
                {
                    CanvasGroup mine = made.GetComponentInParent<CanvasGroup>();
                    if (mine != null && mine.transform.IsChildOf(made.transform.parent)) group.SetValue(entry, mine);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: свойства пункта меню не перенеслись: " + e.Message);
            }
        }

        /// Один раз пишет в журнал, как устроено меню паузы: по этому видно, почему пункт встал так, а не иначе.
        internal static void Dump(string what, Transform root, Transform donor)
        {
            try
            {
                var builder = new System.Text.StringBuilder("Exuvia: " + what + ":");
                DumpNode(root, 0, builder, donor);
                Plugin.Log.LogInfo(builder.ToString());
            }
            catch (Exception) { }
        }

        static void DumpNode(Transform node, int depth, System.Text.StringBuilder builder, Transform donor)
        {
            if (depth > 6) return;
            builder.Append('\n').Append(new string(' ', depth * 2)).Append(node.name);
            RectTransform rectTransform = node as RectTransform;
            if (rectTransform != null) builder.Append(" pos=").Append(rectTransform.anchoredPosition.ToString("F0"))
                .Append(" size=").Append(rectTransform.rect.size.ToString("F0"));
            if (!node.gameObject.activeSelf) builder.Append(" (выкл)");
            var kinds = new List<string>();
            foreach (Component component in node.GetComponents<Component>())
                if (component != null && !(component is Transform) && !(component is CanvasRenderer))
                    kinds.Add(component.GetType().Name);
            if (kinds.Count > 0) builder.Append(" [").Append(string.Join(",", kinds.ToArray())).Append("]");
            // Внутрь пунктов-кнопок не заходим, кроме образца: они одинаковые.
            bool isButton = node.GetComponent<Selectable>() != null;
            if (isButton && (donor == null || (node != donor && !donor.IsChildOf(node)))) return;
            for (int i = 0; i < node.childCount; i++) DumpNode(node.GetChild(i), depth + 1, builder, donor);
        }

        static string Kinds(List<Selectable> list)
        {
            var names = new List<string>();
            foreach (Selectable selectable in list) names.Add(selectable.name + ":" + selectable.GetType().Name);
            return string.Join(", ", names.ToArray());
        }

        /// Кнопки меню паузы (PauseMenuButton) сами знают только «продолжить», «настройки» и «выйти».
        /// Нашим копиям ставим неизвестный игре вид (тогда их нажатие игра пропускает) и перехватываем
        /// нажатие, отмену и стрелки. Что делать — записано в rowActions.
        internal static void PatchPause()
        {
            if (pausePatched) return;
            pausePatched = true;
            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".menu");
            const BindingFlags Own = Priv | BindingFlags.DeclaredOnly;
            const BindingFlags Mine = BindingFlags.Static | BindingFlags.NonPublic;
            playSubmitSound = typeof(MenuSelectable).GetMethod("PlaySubmitSound", Priv);
            TryPatch(harmony, typeof(PauseMenuButton).GetMethod("OnSubmit", Own),
                typeof(MenuHook).GetMethod("PauseSubmit", Mine), "нажатие");
            TryPatch(harmony, typeof(PauseMenuButton).GetMethod("OnCancel", Own),
                typeof(MenuHook).GetMethod("PauseCancel", Mine), "отмена");
            TryPatch(harmony, typeof(Selectable).GetMethod("OnMove", Own), typeof(MenuHook).GetMethod("AnyMove", Mine),
                "стрелки");
        }

        static void TryPatch(HarmonyLib.Harmony harmony, MethodInfo target, MethodInfo prefix, string what)
        {
            try
            {
                if (target == null || prefix == null) throw new Exception("метод не найден");
                harmony.Patch(target, new HarmonyLib.HarmonyMethod(prefix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехват кнопок меню (" + what + ") не поставился: " + e.Message);
            }
        }

        static bool PauseSubmit(PauseMenuButton __instance)
        {
            RowActions actions;
            if (!rowActions.TryGetValue(__instance, out actions)) return true;
            try
            {
                if (!__instance.interactable) return false;
                if (__instance.flashEffect != null)
                {
                    __instance.flashEffect.ResetTrigger("Flash");
                    __instance.flashEffect.SetTrigger("Flash");
                }
                // меню перестаёт слушать клавиши, пока идёт переход
                if (actions.DeselectOnSubmit) __instance.ForceDeselect();
                if (playSubmitSound != null) playSubmitSound.Invoke(__instance, null);
                if (actions.Submit != null) actions.Submit();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: нажатие кнопки меню: " + e.Message);
            }
            return false;
        }

        static bool PauseCancel(PauseMenuButton __instance)
        {
            RowActions actions;
            if (!rowActions.TryGetValue(__instance, out actions) || actions.Cancel == null) return true;
            try { actions.Cancel(); }
            catch (Exception e) { Plugin.Log.LogWarning("Exuvia: отмена в меню: " + e.Message); }
            return false;
        }

        static bool AnyMove(Selectable __instance, AxisEventData eventData)
        {
            if (rowActions.Count == 0 || eventData == null) return true;
            RowActions actions;
            if (!rowActions.TryGetValue(__instance, out actions) || actions.Move == null) return true;
            try { return !actions.Move(eventData.moveDir); }
            catch (Exception) { return true; }
        }

        // ---- главное меню --------------------------------------------------------

        static MenuButton AddToMain(UIManager ui)
        {
            MainMenuOptions mainMenu = ui.mainMenuButtons;
            if (mainMenu == null || mainMenu.quitButton == null) return null;
            MenuButton donor = mainMenu.optionsButton != null ? mainMenu.optionsButton : mainMenu.startButton;
            if (donor == null) return null;

            Selectable above = mainMenu.quitButton.navigation.selectOnUp;
            MenuButton made = CloneBefore(donor, mainMenu.quitButton, above != null ? above : donor) as MenuButton;
            if (made == null) return null;
            Plugin.Log.LogInfo("Exuvia: в главное меню добавлена кнопка «" + Label() + "» (над кнопкой выхода).");
            return made;
        }

        /// В главном меню переходы клавишами заданы вручную, и игра их иногда выставляет заново.
        /// Держим нашу кнопку в цепочке прямо над кнопкой выхода.
        static void LinkMain(UIManager ui)
        {
            MainMenuOptions mainMenu = ui.mainMenuButtons;
            if (mainMenu == null || mainMenu.quitButton == null || mainButton == null || !MainMenuShown(ui)) return;
            MenuButton quit = mainMenu.quitButton;
            Navigation quitNav = quit.navigation;
            if (quitNav.selectOnUp == mainButton) return;

            Selectable above = quitNav.selectOnUp;
            Navigation mine = mainButton.navigation;
            mine.mode = Navigation.Mode.Explicit;
            mine.selectOnUp = above;
            mine.selectOnDown = quit;
            mainButton.navigation = mine;

            if (above != null && above != mainButton)
            {
                Navigation nav = above.navigation;
                nav.selectOnDown = mainButton;
                above.navigation = nav;
            }
            quitNav.selectOnUp = mainButton;
            quit.navigation = quitNav;
        }

        // ---- копия кнопки --------------------------------------------------------

        /// Делает копию кнопки donor и ставит её на место кнопки next, сдвигая next ниже.
        /// prev — кнопка над next (по ней считаем шаг между пунктами).
        static Selectable CloneBefore(Selectable donor, Selectable next, Selectable prev)
        {
            return CloneBefore(donor, next, prev, null);
        }

        /// centred — все кнопки меню: если раскладка ручная, список раздвигается в обе стороны и остаётся по центру.
        static Selectable CloneBefore(Selectable donor, Selectable next, Selectable prev, List<Selectable> centred)
        {
            // Пункт меню может быть обёрнут в свой контейнер: копируем весь пункт, а не только кнопку.
            Transform container = CommonParent(donor.transform, next.transform);
            if (container == null) return null;
            Transform donorUnit = ChildUnder(container, donor.transform);
            Transform nextUnit = ChildUnder(container, next.transform);
            Transform prevUnit = prev != null ? ChildUnder(container, prev.transform) : null;
            if (donorUnit == null || nextUnit == null || donorUnit == container) return null;

            GameObject copy = UnityEngine.Object.Instantiate(donorUnit.gameObject, container);
            copy.name = "Exuvia Skins Button";
            Selectable button = (Selectable)copy.GetComponentInChildren(donor.GetType(), true);
            if (button == null)
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }

            // Убираем с копии всё, что тянуло действие и подпись образца.
            // У EventTrigger убираем только «нажатие» (там действие образца); наведение мышью и отмена остаются как у
            // родных кнопок.
            foreach (EventTrigger trigger in copy.GetComponentsInChildren<EventTrigger>(true))
                if (trigger.triggers != null)
                    trigger.triggers.RemoveAll(delegate(EventTrigger.Entry e)
                    {
                        return e == null || e.eventID == EventTriggerType.Submit
                            || e.eventID == EventTriggerType.PointerClick;
                    });
            foreach (AutoLocalizeTextUI component in copy.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (MenuButtonListCondition component in copy.GetComponentsInChildren<MenuButtonListCondition>(true))
                UnityEngine.Object.DestroyImmediate(component);

            MenuButton asMenu = button as MenuButton;
            PauseMenuButton asPause = button as PauseMenuButton;
            if (asMenu != null)
            {
                if (asMenu.OnSubmitPressed == null) asMenu.OnSubmitPressed = new UnityEngine.Events.UnityEvent();
                for (int i = 0; i < asMenu.OnSubmitPressed.GetPersistentEventCount(); i++)
                    asMenu.OnSubmitPressed.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                asMenu.OnSubmitPressed.RemoveAllListeners();
                asMenu.OnSubmitPressed.AddListener(delegate { if (open != null) open(asMenu); });
                // при нажатии выбор снимается: меню под окном не реагирует на клавиши
                asMenu.buttonType = MenuButton.MenuButtonType.Proceed;
            }
            else if (asPause != null)
            {
                // вид, которого игра не знает: её собственное действие не сработает
                asPause.pauseButtonType = UnknownType;
                Selectable self = asPause;
                Register(asPause, new RowActions { Submit = delegate { if (open != null) open(self); },
                    DeselectOnSubmit = true });
            }
            else
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }
            button.interactable = true;

            Text label = null;
            foreach (Text textLabel in copy.GetComponentsInChildren<Text>(true))
                if (label == null || textLabel.fontSize > label.fontSize) label = textLabel;
            if (label != null) label.text = Label();

            // Место: если пункты раскладывает сама игра (LayoutGroup) — достаточно порядка.
            copy.transform.SetSiblingIndex(nextUnit.GetSiblingIndex());
            if (container.GetComponent<LayoutGroup>() == null)
            {
                RectTransform nextRt = nextUnit as RectTransform, copyRt = copy.transform as RectTransform, prevRt =
                    prevUnit as RectTransform;
                if (nextRt != null && copyRt != null)
                {
                    Vector2 step = prevRt != null
                        && prevRt != nextRt ? nextRt.anchoredPosition - prevRt.anchoredPosition : new Vector2(0f,
                        -nextRt.rect.height);
                    if (step.sqrMagnitude < 1f) step = new Vector2(0f, -Mathf.Max(40f, nextRt.rect.height));
                    copyRt.anchorMin = nextRt.anchorMin;
                    copyRt.anchorMax = nextRt.anchorMax;
                    copyRt.pivot = nextRt.pivot;
                    copyRt.anchoredPosition = nextRt.anchoredPosition;
                    nextRt.anchoredPosition = nextRt.anchoredPosition + step;
                    if (centred != null)
                    {
                        // Список стал длиннее на шаг: поднимаем все пункты на полшага, середина остаётся на месте.
                        var moved = new HashSet<Transform>();
                        Vector2 half = step * 0.5f;
                        foreach (Selectable other in centred)
                        {
                            RectTransform unit = other != null ? ChildUnder(container,
                                other.transform) as RectTransform : null;
                            if (unit != null && moved.Add(unit)) unit.anchoredPosition = unit.anchoredPosition - half;
                        }
                        if (moved.Add(copyRt)) copyRt.anchoredPosition = copyRt.anchoredPosition - half;
                    }
                }
            }
            copy.SetActive(true);
            return button;
        }

        static Transform CommonParent(Transform a, Transform b)
        {
            if (a == b) return a.parent;
            for (Transform node = a.parent; node != null; node = node.parent)
                if (b.IsChildOf(node)) return node;
            return null;
        }

        /// Прямой ребёнок container, внутри которого лежит t.
        static Transform ChildUnder(Transform container, Transform node)
        {
            Transform cur = node;
            while (cur != null && cur.parent != container) cur = cur.parent;
            return cur;
        }
    }
}
