// ============================================================================
//  T2DEngine.Scan.cs — часть класса T2DEngine: обход сцены и слежение за объектами.
//  Общее описание — в начале T2DEngine.cs.
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
        // ---- обход сцены -------------------------------------------------------

        static void Slice(int count)
        {
            if (sliceSr == null && sliceImg == null) return;
            int nSr = sliceSr != null ? sliceSr.Length : 0;
            int nImg = sliceImg != null ? sliceImg.Length : 0;
            int end = (int)Math.Min((long)sliceAt + count, (long)nSr + nImg);
            for (; sliceAt < end; sliceAt++)
            {
                if (sliceAt < nSr)
                {
                    SpriteRenderer renderer = sliceSr[sliceAt];
                    if (renderer == null) continue;
                    int found = Handle(renderer, null, renderer.sprite);
                    int id = renderer.GetInstanceID();
                    // Объект видим впервые: если он анимируется, за ним надо следить (см. шапку файла).
                    if (seen.Add(id)) { if (found != Replaced
                        && renderer.GetComponentInParent<Animator>(true) != null) Watch(renderer, null, id, found); }
                    else if (found == Touched && coldById.ContainsKey(id)) MarkHot(renderer, null);
                    Recheck(id, renderer.gameObject);
                }
                else
                {
                    Image img = sliceImg[sliceAt - nSr];
                    if (img == null) continue;
                    int found = Handle(null, img, img.sprite);
                    int id = img.GetInstanceID();
                    if (seen.Add(id)) { if (found != Replaced
                        && img.GetComponentInParent<Animator>(true) != null) Watch(null, img, id, found); }
                    else if (found == Touched && coldById.ContainsKey(id)) MarkHot(null, img);
                    Recheck(id, img.gameObject);
                }
            }
            if (sliceAt >= nSr + nImg)
            {
                sliceSr = null;
                sliceImg = null;
                if (vanillaOfOurs.Count != loggedCount)
                {
                    loggedCount = vanillaOfOurs.Count;
                    Plugin.Log.LogInfo("Exuvia: T2D: объектов на сцене " + (nSr + nImg) + ", своих спрайтов "
                        + loggedCount
                        + ", картинок в памяти " + textures.Count + " (~" + Megabytes.ToString("0")
                            + " МБ), следим каждый кадр за "
                        + hot.Count + " (из них включено " + awakeHot + "), по очереди за " + coldById.Count
                            + " (включено " + AwakeCold() + ")"
                        + (wakeFailures > 0 ? ", сбоев будильника " + wakeFailures : "") + "; " + Mode(0, "спрайты")
                            + ", " + Mode(1, "интерфейс"));
                }
            }
        }

        /// Группа «каждый кадр». Аниматор возвращает родной спрайт — ставим свой снова.
        /// Если уведомлениям игры уже верим, объект проверяется только раз в SafetyFrames кадров (страховка).
        static void Enforce()
        {
            if (hotChanged)
            {
                hotChanged = false;
                hotList.Clear();
                hotList.AddRange(hot.Values);
            }
            bool dead = false;
            bool work = wanted.Count > 0;
            int frame = Time.frameCount;
            int awake = 0;
            for (int i = 0; i < hotList.Count; i++)
            {
                Holder holder = hotList[i];
                if (holder.Asleep) continue; // объект выключен: его не видно, а при включении нас позовёт T2DWake
                awake++;
                if (trusted[holder.IsImg ? 1 : 0] && (i + frame) % SafetyFrames != 0) continue;
                if (!Check(holder, work)) dead = true;
            }
            awakeHot = awake;
            if (dead)
            {
                var gone = new List<int>();
                foreach (KeyValuePair<int, Holder> pair in hot) if (pair.Value.Dead) gone.Add(pair.Key);
                foreach (int id in gone) hot.Remove(id);
                hotChanged = true;
            }
        }

        /// Группа «по очереди»: за кадр проверяем такую долю, чтобы весь список обходился за ColdPeriod секунд.
        /// Объект показал текстуру, для которой в паке есть картинки, — переводим его в группу «каждый кадр».
        static void PollCold()
        {
            if (cold.Count == 0 || wanted.Count == 0) return;
            bool calm = trusted[0] && trusted[1];
            float share = Mathf.Clamp01(Time.unscaledDeltaTime / (calm ? SafetyColdPeriod : ColdPeriod));
            int count = Mathf.Max(1, Mathf.CeilToInt(cold.Count * share));
            for (int k = 0; k < count && cold.Count > 0; k++)
            {
                if (coldAt >= cold.Count) { coldAt = 0; coldPass++; }
                Holder holder = cold[coldAt];
                bool drop = holder.Hot;
                if (!drop && holder.Asleep) { coldAt++; continue; } // выключен — см. Enforce
                if (!drop)
                {
                    // Верим уведомлениям только по одному виду объектов: такие здесь проверяем через девять обходов на
                    // десятый.
                    if (!calm && trusted[holder.IsImg ? 1 : 0] && coldPass % 10 != 0) { coldAt++; continue; }
                    if (!Check(holder, true)) { drop = true; coldById.Remove(holder.Id); } // объект уничтожен
                    // Handle сам перевёл его в «каждый кадр»
                    else if (holder.Hot) drop = true;
                    else if (lastFound != None) { MarkHot(holder.Sr, holder.Img); drop = true; }
                }
                if (drop)
                {
                    cold[coldAt] = cold[cold.Count - 1];
                    cold.RemoveAt(cold.Count - 1);
                }
                else coldAt++;
            }
        }

        static int lastFound;

        /// Проверка одного объекта: сверяем уведомления и ставим нашу картинку, если надо.
        /// false — объект уничтожен. Что нашлось (None / Touched / Replaced) — в lastFound.
        static bool Check(Holder holder, bool work)
        {
            Sprite cur;
            if (!holder.IsImg)
            {
                if (holder.Sr == null) return false;
                cur = holder.Sr.sprite;
            }
            else
            {
                if (holder.Img == null) return false;
                cur = holder.Img.sprite;
            }
            lastFound = None;
            if (!work)
            {
                holder.Polled = false; // подменять сейчас нечего — сверку уведомлений начнём заново, когда будет что
                return true;
            }
            int kind = holder.IsImg ? 1 : 0;
            int curId = cur != null ? cur.GetInstanceID() : 0;

            // Картинка сменилась с прошлой проверки: пришло ли об этом уведомление?
            if (holder.Polled && curId != holder.LastSprite)
            {
                if (holder.Events != holder.EventsAtPoll) seenChanges[kind]++;
                else
                {
                    // Без уведомления. У выключенного объекта это ожидаемо (аниматор вернул исходную картинку),
                    // и при включении нас разбудит T2DWake. У включённого — настоящий пропуск.
                    GameObject obj = holder.IsImg ? holder.Img.gameObject : holder.Sr.gameObject;
                    if (!obj.activeInHierarchy) quietChanges[kind]++;
                    else
                    {
                        missedChanges[kind]++;
                        if (missLogged < 12)
                        {
                            missLogged++;
                            Animator anim = obj.GetComponentInParent<Animator>(true);
                            Plugin.Log.LogInfo("Exuvia: T2D: смена без уведомления: объект " + PathOf(obj.transform)
                                + (holder.IsImg ? " (интерфейс)" : " (спрайт)")
                                + ", новая картинка " + (cur != null ? cur.name : "нет") + ", аниматор "
                                + (anim == null ? "нет" : (anim.isActiveAndEnabled ? "работает" : "выключен"))
                                    + ", группа " + (holder.Hot ? "каждый кадр" : "по очереди")
                                + (holder.IsImg ? (holder.Img.enabled ? ", картинка включена" : ", картинка выключена")
                                           : (holder.Sr.enabled ? ", рисовальщик включён" : ", рисовальщик выключен")
                                               + (holder.Sr.isVisible ? ", виден" : ", не виден")));
                        }
                    }
                }
            }
            holder.Polled = true;
            holder.EventsAtPoll = holder.Events;

            finalSprite = curId;
            lastFound = holder.IsImg ? Handle(null, holder.Img, cur) : Handle(holder.Sr, null, cur);
            holder.LastSprite = finalSprite;
            return true;
        }

        /// Решаем, можно ли верить уведомлениям игры (отдельно для спрайтов и для интерфейса).
        static void Judge()
        {
            for (int kind = 0; kind < 2; kind++)
            {
                if (refused[kind]) continue;
                if (missedChanges[kind] > 0)
                {
                    refused[kind] = true;
                    bool was = trusted[kind];
                    trusted[kind] = false;
                    Plugin.Log.LogInfo("Exuvia: T2D: " + (kind == 0 ? "спрайты" : "интерфейс")
                        + ": смена картинки прошла без уведомления от игры (с уведомлением: "
                        + seenChanges[kind] + ") — " + (was ? "возвращаюсь к проверке каждый кадр."
                            : "остаюсь на проверке каждый кадр."));
                }
                else if (!trusted[kind] && seenChanges[kind] >= TrustAfter)
                {
                    trusted[kind] = true;
                    Plugin.Log.LogInfo("Exuvia: T2D: " + (kind == 0 ? "спрайты" : "интерфейс")
                        + ": игра исправно сообщает о смене картинок (" + seenChanges[kind]
                        + " из " + seenChanges[kind] + ") — проверка каждый кадр заменена редкой страховочной.");
                }
            }
        }

        static string Mode(int kind, string what)
        {
            string mode = trusted[kind] ? "по уведомлениям" : (refused[kind] ? "каждый кадр (уведомления подвели)"
                : "каждый кадр (уведомления проверяются)");
            return what + ": " + mode + ", смен с уведомлением " + seenChanges[kind] + ", без " + missedChanges[kind]
                + ", у выключенных " + quietChanges[kind];
        }

        static string PathOf(Transform node)
        {
            string path = node.name;
            for (int i = 0; i < 3 && node.parent != null; i++)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return path;
        }

        /// Просим игру сообщать о сменах картинки у этого объекта: уведомление о смене спрайта
        /// (только для SpriteRenderer) и компонент-будильник T2DWake, который говорит, включён объект или нет.
        static void Listen(Holder holder)
        {
            if (!listening.Add(holder.Id)) return;
            try
            {
                if (holder.Sr != null)
                {
                    if (onSpriteChanged == null) onSpriteChanged = OnSpriteChanged;
                    holder.Sr.RegisterSpriteChangeCallback(onSpriteChanged);
                }
            }
            catch (Exception) { }
            try
            {
                GameObject obj = holder.Sr != null ? holder.Sr.gameObject : holder.Img.gameObject;
                T2DWake wake = obj.GetComponent<T2DWake>();
                if (wake == null) wake = obj.AddComponent<T2DWake>();
                if (wake != null && wake.enabled)
                {
                    holder.Wake = true;
                    holder.Asleep = !obj.activeInHierarchy;
                }
            }
            catch (Exception)
            {
                holder.Wake = false;
                holder.Asleep = false;
            }
        }

        static Holder Find(int id)
        {
            Holder holder;
            if (hot.TryGetValue(id, out holder) || coldById.TryGetValue(id, out holder)) return holder;
            return null;
        }

        /// Будильник: объект выключили. Пока он выключен, проверять его не будем.
        internal static void Slept(int id)
        {
            Holder holder = Find(id);
            if (holder != null && holder.Wake) holder.Asleep = true;
        }

        /// Обход сцены увидел объект. Если будильник считает его выключенным, а он включён, —
        /// будильнику у этого объекта больше не верим и проверяем объект как раньше.
        static void Recheck(int id, GameObject obj)
        {
            Holder holder = Find(id);
            if (holder == null || !holder.Asleep) return;
            if (!obj.activeInHierarchy) return;
            holder.Asleep = false;
            holder.Wake = false;
            if (wakeFailures++ < 5) Plugin.Log.LogInfo("Exuvia: T2D: будильник не сработал у объекта "
                + PathOf(obj.transform) + " — проверяю его каждый кадр.");
        }

        /// Объект с будильником только что включили (или будильник только что повесили на включённый объект).
        internal static void Woke(SpriteRenderer renderer, Image img)
        {
            if (renderer == null && img == null) return;
            Holder woke = Find(renderer != null ? renderer.GetInstanceID() : img.GetInstanceID());
            if (woke != null) woke.Asleep = false;
            if (busy) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (renderer != null)
            {
                Noted(renderer.GetInstanceID());
                if (wanted.Count > 0)
                {
                    try { Handle(renderer, null, renderer.sprite); }
                    catch (Exception) { }
                }
            }
            else if (img != null)
            {
                Noted(img.GetInstanceID());
                if (wanted.Count > 0)
                {
                    try { Handle(null, img, img.sprite); }
                    catch (Exception) { }
                }
            }
            HookCalls++;
            HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        /// Уведомление игры: у SpriteRenderer сменилась картинка (в том числе её сменил аниматор).
        static void OnSpriteChanged(SpriteRenderer renderer)
        {
            if (busy || renderer == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Noted(renderer.GetInstanceID());
            if (wanted.Count > 0)
            {
                try { Handle(renderer, null, renderer.sprite); }
                catch (Exception) { }
            }
            HookCalls++;
            HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        /// Отмечаем у объекта, что про смену его картинки нам сообщили.
        static void Noted(int id)
        {
            Holder holder;
            if (hot.TryGetValue(id, out holder) || coldById.TryGetValue(id, out holder)) holder.Events++;
        }

        /// Впервые увиденный анимированный объект: в какую группу его поставить.
        static void Watch(SpriteRenderer renderer, Image img, int id, int found)
        {
            if (found != None) { MarkHot(renderer, img); return; }
            if (hot.ContainsKey(id) || coldById.ContainsKey(id)) return;
            var holder = new Holder();
            holder.Sr = renderer;
            holder.Img = img;
            holder.Id = id;
            holder.IsImg = renderer == null;
            cold.Add(holder);
            coldById[id] = holder;
            Listen(holder);
        }

        static Holder MarkHot(SpriteRenderer renderer, Image img)
        {
            int id = renderer != null ? renderer.GetInstanceID() : img.GetInstanceID();
            Holder holder;
            if (hot.TryGetValue(id, out holder)) return holder;
            if (coldById.TryGetValue(id, out holder)) coldById.Remove(id); // из списка cold его уберёт PollCold
            else
            {
                holder = new Holder();
                holder.Sr = renderer;
                holder.Img = img;
                holder.Id = id;
                holder.IsImg = renderer == null;
            }
            holder.Hot = true;
            hot[id] = holder;
            hotChanged = true;
            Listen(holder);
            return holder;
        }
    }
}
