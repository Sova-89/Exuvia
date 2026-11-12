// ============================================================================
//  SkinScreen.Build.cs — часть класса SkinScreen: сборка экрана из копии меню паузы.
//  Общее описание — в начале SkinScreen.cs.
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
        // ---- сборка экрана -------------------------------------------------------

        static void Build(UIManager ui)
        {
            MenuScreen source = ui.pauseMenuScreen;
            if (source == null) throw new Exception("нет экрана паузы");

            // Копию делаем внутри выключенного объекта: её части не «просыпаются», пока мы их переделываем.
            var holder = new GameObject("Exuvia.Holder");
            holder.SetActive(false);
            holder.transform.SetParent(source.transform.parent, false);
            GameObject copy = null;
            try
            {
                copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
                copy.name = "Exuvia Skins Screen";
                copy.SetActive(false);
                Assemble(copy);
                copy.transform.SetParent(source.transform.parent, false);
                copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            }
            catch (Exception)
            {
                if (copy != null) UnityEngine.Object.Destroy(copy);
                ClearBuilt();
                throw;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
            root = copy;
            Plugin.Log.LogInfo("Exuvia: экран скинов в стиле игры собран (строк на экране: " + rows.Count + ").");
        }

        static void Assemble(GameObject copy)
        {
            screen = copy.GetComponent<MenuScreen>();
            if (screen == null) throw new Exception("у экрана паузы нет MenuScreen");
            // Список кнопок игры нам не нужен: переходы между своими строками задаём сами.
            foreach (MenuButtonList list in copy.GetComponentsInChildren<MenuButtonList>(true))
                UnityEngine.Object.DestroyImmediate(list);
            CanvasGroup group = copy.GetComponent<CanvasGroup>();
            if (group != null) { group.alpha = 0f; group.interactable = false; }

            PauseMenuButton[] old = copy.GetComponentsInChildren<PauseMenuButton>(true);
            if (old.Length == 0) throw new Exception("в экране паузы нет кнопок");
            PauseMenuButton template = null;
            foreach (PauseMenuButton pauseButton in old)
                if (template == null && pauseButton.name == "OptionsButton") template = pauseButton;
            foreach (PauseMenuButton pauseButton in old)
                if (template == null && pauseButton.GetComponentInChildren<MenuButtonListCondition>(true) == null)
                    template = pauseButton;
            if (template == null) template = old[0];

            Transform list0 = template.transform.parent;
            if (list0 == null || list0.GetComponent<LayoutGroup>() == null)
                throw new Exception("кнопки паузы лежат не в раскладке");
            // Снаружи списка у игры ещё одна раскладка (узор, список, узор) — туда пойдут заголовок и пояснение.
            Transform outer = list0 != copy.transform && list0.parent != null
                && list0.parent.GetComponent<LayoutGroup>() != null ? list0.parent : list0;

            // Образец строки: родная кнопка без своего действия и без перевода подписи.
            GameObject sample = template.gameObject;
            foreach (AutoLocalizeTextUI component in sample.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (MenuButtonListCondition component in sample.GetComponentsInChildren<MenuButtonListCondition>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (EventTrigger trigger in sample.GetComponentsInChildren<EventTrigger>(true))
                if (trigger.triggers != null)
                    trigger.triggers.RemoveAll(delegate(EventTrigger.Entry e)
                    {
                        return e == null || e.eventID == EventTriggerType.Submit
                            || e.eventID == EventTriggerType.PointerClick || e.eventID == EventTriggerType.Cancel;
                    });
            template.pauseButtonType = MenuHook.UnknownType;
            template.interactable = true;
            sample.SetActive(true);
            Text sampleLabel = Biggest(sample);
            if (sampleLabel == null) throw new Exception("у кнопки паузы нет подписи");
            baseColor = sampleLabel.color;
            sampleLabel.supportRichText = true;

            // Строки скинов.
            for (int i = 0; i < VisibleRows; i++)
            {
                GameObject obj = UnityEngine.Object.Instantiate(sample, list0);
                obj.name = "Exuvia Skin Row " + i;
                var rect = obj.transform as RectTransform;
                if (rect != null && rect.sizeDelta.y > RowHeight)
                    rect.sizeDelta = new Vector2(rect.sizeDelta.x, RowHeight);
                PauseMenuButton button = obj.GetComponent<PauseMenuButton>();
                int row = i;
                MenuHook.Register(button, new MenuHook.RowActions
                {
                    Submit = delegate { Submit(row); },
                    Cancel = delegate { Back(true); },
                    Move = delegate(MoveDirection dir) { return Move(row, dir); }
                });
                rows.Add(button);
                rowLabels.Add(Biggest(obj));
            }

            // «Назад».
            GameObject backGo = UnityEngine.Object.Instantiate(sample, list0);
            backGo.name = "Exuvia Back";
            backRow = backGo.GetComponent<PauseMenuButton>();
            MenuHook.Register(backRow, new MenuHook.RowActions
            {
                Submit = delegate { Back(false); },
                Cancel = delegate { Back(true); },
                Move = delegate(MoveDirection dir) { return MoveFromBack(dir); },
                DeselectOnSubmit = true
            });
            SetText(Biggest(backGo), L.T("Назад", "Back"));

            // Подписи: заголовок над верхним узором, «· · ·» по краям списка, пояснение под нижним узором.
            title = MakeText(sampleLabel, outer, "Exuvia Title", 1000f, 90f, false);
            SetText(title, MenuHook.Label());
            dotsTop = MakeText(sampleLabel, list0, "Exuvia More Above", 1000f, DotsHeight, false);
            dotsBottom = MakeText(sampleLabel, list0, "Exuvia More Below", 1000f, DotsHeight, false);
            SetText(dotsTop, "·  ·  ·");
            SetText(dotsBottom, "·  ·  ·");
            info = MakeText(sampleLabel, outer, "Exuvia Info", InfoWidth, InfoHeight, true);
            preview = MakePreview(copy.transform);

            // Родные кнопки больше не нужны.
            foreach (PauseMenuButton pauseButton in old) UnityEngine.Object.DestroyImmediate(pauseButton.gameObject);

            SetAlpha(info, 0.8f);

            // Порядок сверху вниз: заголовок, [узор], «· · ·», строки, «· · ·», «Назад», [узор], пояснение.
            title.transform.SetAsFirstSibling();
            int first = outer == list0 ? 1 : 0;
            dotsTop.transform.SetSiblingIndex(first);
            for (int i = 0; i < rows.Count; i++) rows[i].transform.SetSiblingIndex(first + 1 + i);
            dotsBottom.transform.SetSiblingIndex(first + 1 + rows.Count);
            backGo.transform.SetSiblingIndex(first + 2 + rows.Count);
            info.transform.SetAsLastSibling();
        }

        /// Место под картинку-превью: справа от списка, вне раскладки экрана.
        static RawImage MakePreview(Transform parent)
        {
            var obj = new GameObject("Exuvia Preview", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            obj.layer = parent.gameObject.layer;
            var rect = (RectTransform)obj.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(PreviewX, PreviewY);
            rect.sizeDelta = new Vector2(PreviewSize, PreviewSize);
            obj.AddComponent<LayoutElement>().ignoreLayout = true;
            var image = obj.GetComponent<RawImage>();
            image.raycastTarget = false;
            image.enabled = false;
            return image;
        }

        static Text Biggest(GameObject obj)
        {
            Text best = null;
            foreach (Text label in obj.GetComponentsInChildren<Text>(true))
                if (best == null || label.fontSize > best.fontSize) best = label;
            return best;
        }

        /// Надпись тем же шрифтом, что у кнопок меню: копия подписи кнопки без стрелок-указателей.
        static Text MakeText(Text sample, Transform parent, string name, float width, float height, bool wrap)
        {
            GameObject obj = UnityEngine.Object.Instantiate(sample.gameObject, parent);
            obj.name = name;
            for (int i = obj.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(obj.transform.GetChild(i).gameObject);
            // Размер шрифта и место этих надписей задаём сами.
            foreach (ContentSizeFitter component in obj.GetComponents<ContentSizeFitter>())
                UnityEngine.Object.DestroyImmediate(component);
            foreach (ChangeTextFontScaleOnHandHeld component in obj.GetComponents<ChangeTextFontScaleOnHandHeld>())
                UnityEngine.Object.DestroyImmediate(component);
            if (wrap) foreach (FixVerticalAlign component in obj.GetComponents<FixVerticalAlign>()) UnityEngine.Object.DestroyImmediate(component);

            Text text = obj.GetComponent<Text>();
            text.text = "";
            text.raycastTarget = false;
            text.supportRichText = true;
            text.resizeTextForBestFit = false;
            text.alignment = wrap ? TextAnchor.UpperCenter : TextAnchor.MiddleCenter;
            text.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            if (wrap) text.lineSpacing = 1f;

            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            LayoutElement layout = obj.GetComponent<LayoutElement>();
            if (layout == null) layout = obj.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.preferredWidth = width;
            layout.flexibleHeight = 0f;
            layout.flexibleWidth = 0f;
            obj.SetActive(true);
            return text;
        }
    }
}
