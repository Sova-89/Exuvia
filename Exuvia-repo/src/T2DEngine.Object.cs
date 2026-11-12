// ============================================================================
//  T2DEngine.Object.cs — часть класса T2DEngine: подмена у одного объекта и перехват присвоения спрайта.
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
        // ---- один объект -------------------------------------------------------

        /// Ставит объекту нашу картинку, если для его спрайта она есть. Возвращает None, Touched или Replaced.
        static int Handle(SpriteRenderer renderer, Image img, Sprite cur)
        {
            if (cur == null) return None;
            int id = cur.GetInstanceID();
            if (vanillaOfOurs.ContainsKey(id))
            {
                // объекту достался наш спрайт (например, скопировали с другого объекта)
                MarkHot(renderer, img).LastSprite = id;
                return Replaced;
            }

            Rep rep;
            if (!byVanilla.TryGetValue(id, out rep))
            {
                bool later;
                rep = Build(cur, out later);
                if (later)
                {
                    var holder = new Holder();
                    holder.Sr = renderer;
                    holder.Img = img;
                    holder.IsImg = renderer == null;
                    retry.Add(holder);
                    return Touched; // картинка есть, просто ещё не расшифрована
                }
                byVanilla[id] = rep;
            }
            if (rep.Ours == null) return ReferenceEquals(rep, NoRepTouched) ? Touched : None;

            busy = true;
            finalSprite = id;
            try
            {
                if (renderer != null) renderer.sprite = rep.Ours; else img.sprite = rep.Ours;
                sets++;
                finalSprite = rep.Ours.GetInstanceID();
            }
            catch (Exception e)
            {
                if (failedSets++ < 5) Plugin.Log.LogWarning("Exuvia: T2D: не поставился спрайт " + cur.name + ": "
                    + e.Message);
            }
            finally
            {
                busy = false;
            }
            // Картинку поменяли мы сами — запоминаем это у объекта, чтобы сверка уведомлений не сочла смену
            // «пропущенной».
            MarkHot(renderer, img).LastSprite = finalSprite;
            return Replaced;
        }

        /// Создаёт свой спрайт на замену родному. later = true — не хватило времени кадра, попробуем позже.
        static Rep Build(Sprite vanilla, out bool later)
        {
            later = false;
            Texture2D tex2d = vanilla.texture;
            if (tex2d == null) return NoRep;
            string texName = tex2d.name;
            T2DFile file = wanted.Find(texName, vanilla.name);
            if (file == null) return wanted.Touches(texName) ? NoRepTouched : NoRep;

            Texture2D tex = GetTexture(file, tex2d, out later);
            if (tex == null) return later ? null : NoRepTouched;

            try
            {
                // Размер и точка опоры — как у Stitchwork: по умолчанию картинка показывается в масштабе игры
                // (холст больше — рисунок крупнее), точка опоры в той же доле холста, что у родного спрайта.
                Rect vr = vanilla.rect;
                Vector2 vp = vanilla.pivot;
                float ppu = vanilla.pixelsPerUnit;
                Rect rect;
                Vector2 pivot;
                bool hasRect = vr.width > 0f && vr.height > 0f;

                if (file.Enforced && tex2d.width > 0 && tex2d.height > 0 && !T2DNames.IsAtlas(texName))
                {
                    // Картинка заменяет всю родную текстуру (лист): берём из неё ту же долю, что занимал спрайт.
                    float sx = (float)tex.width / tex2d.width, sy = (float)tex.height / tex2d.height;
                    rect = new Rect(vr.x * sx, vr.y * sy, vr.width * sx, vr.height * sy);
                    rect.width = Mathf.Min(rect.width, tex.width - rect.x);
                    rect.height = Mathf.Min(rect.height, tex.height - rect.y);
                    pivot = new Vector2(vr.width > 0f ? vp.x / vr.width : 0.5f, vr.height > 0f ? vp.y / vr.height
                        : 0.5f);
                    ppu *= sx;
                }
                else
                {
                    rect = new Rect(0f, 0f, tex.width, tex.height);
                    pivot = hasRect ? new Vector2(vp.x / vr.width, vp.y / vr.height) : new Vector2(0.5f, 0.5f);
                    if (file.Enforced && vr.width > 0f) ppu = ppu * tex.width / vr.width; // тег @e: держим размер игры
                }
                if (file.Scale != 1f) ppu /= file.Scale;
                if (rect.width < 1f || rect.height < 1f || ppu <= 0f) return NoRepTouched;

                bool sameSize = hasRect && Mathf.Abs(tex.width - vr.width) < 0.5f
                    && Mathf.Abs(tex.height - vr.height) < 0.5f;
                Sprite sprite = Sprite.Create(tex, rect, pivot, ppu, 0, SpriteMeshType.FullRect, sameSize
                    ? vanilla.border : Vector4.zero);
                sprite.name = vanilla.name;
                sprite.hideFlags = HideFlags.HideAndDontSave;

                var rep = new Rep();
                rep.Vanilla = vanilla;
                rep.Ours = sprite;
                vanillaOfOurs[sprite.GetInstanceID()] = vanilla;
                return rep;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: T2D: " + file.Path + ": " + e.Message);
                return NoRepTouched;
            }
        }

        static Texture2D GetTexture(T2DFile file, Texture template, out bool later)
        {
            later = false;
            Tex t;
            if (textures.TryGetValue(file.Path, out t) && t.Len == file.Len && t.Ticks == file.Ticks)
                return t.Failed ? null : t.T;

            // Расшифровка PNG стоит времени: за кадр делаем немного, остальное — на следующих кадрах.
            if (clock != null && clock.Elapsed.TotalMilliseconds > budget)
            {
                later = true;
                return null;
            }

            if (t != null && t.T != null) UnityEngine.Object.Destroy(t.T);
            t = new Tex();
            t.Len = file.Len;
            t.Ticks = file.Ticks;
            textures[file.Path] = t;
            try
            {
                byte[] bytes = File.ReadAllBytes(file.Path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes, true))
                {
                    UnityEngine.Object.Destroy(tex);
                    t.Failed = true;
                    Plugin.Log.LogWarning("Exuvia: T2D: не читается " + file.Path);
                    return null;
                }
                tex.name = "Exuvia T2D " + file.Key; // не как у игры: чтобы другие моды не приняли её за родную
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.wrapMode = TextureWrapMode.Clamp;
                if (template != null)
                {
                    tex.filterMode = template.filterMode;
                    tex.anisoLevel = template.anisoLevel;
                }
                t.T = tex;
                return tex;
            }
            catch (Exception e)
            {
                t.Failed = true;
                Plugin.Log.LogWarning("Exuvia: T2D: " + file.Path + ": " + e.Message);
                return null;
            }
        }

        // ---- перехват присвоения спрайта ---------------------------------------

        static void Patch()
        {
            patched = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".t2d");
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                harmony.Patch(HarmonyLib.AccessTools.PropertySetter(typeof(SpriteRenderer), "sprite"), null,
                    new HarmonyLib.HarmonyMethod(typeof(T2DEngine).GetMethod("SrSet", flags)));
                harmony.Patch(HarmonyLib.AccessTools.PropertySetter(typeof(Image), "sprite"), null,
                    new HarmonyLib.HarmonyMethod(typeof(T2DEngine).GetMethod("ImgSet", flags)));

                // Аниматор меняет картинку интерфейса мимо обычного присвоения, но после каждого своего шага
                // зовёт у неё OnDidApplyAnimationProperties — это и есть наше уведомление для Image.
                try
                {
                    MethodInfo applied = typeof(Image).GetMethod("OnDidApplyAnimationProperties",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (applied == null) throw new Exception("метод не найден");
                    harmony.Patch(applied, null, new HarmonyLib.HarmonyMethod(typeof(T2DEngine).GetMethod("ImgAnimated",
                        flags)));
                }
                catch (Exception e)
                {
                    refused[1] = true; // без уведомлений картинки интерфейса проверяем каждый кадр
                    Plugin.Log.LogWarning("Exuvia: T2D: уведомление об анимации картинок интерфейса не поставилось ("
                        + e.Message + "), проверяю их каждый кадр.");
                }
            }
            catch (Exception e)
            {
                // Без перехвата всё равно работает обход сцены, просто подмена может запаздывать на пару секунд.
                Plugin.Log.LogWarning("Exuvia: T2D: перехват смены спрайта не поставился (" + e.Message
                    + "), работаю обходом сцены.");
            }
        }

        // Замер: сколько раз игра меняла картинку у объектов и сколько времени мы на это потратили
        // (в тиках Stopwatch; читает и обнуляет Plugin для журнала).
        internal static int HookCalls;
        internal static long HookTicks;

        static void SrSet(SpriteRenderer __instance, Sprite value)
        {
            if (busy || wanted.Count == 0 || __instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Noted(__instance.GetInstanceID());
            if (value != null)
            {
                try { Handle(__instance, null, value); }
                catch (Exception) { }
            }
            HookCalls++;
            HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        static void ImgSet(Image __instance, Sprite value)
        {
            if (busy || wanted.Count == 0 || __instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Noted(__instance.GetInstanceID());
            if (value != null)
            {
                try { Handle(null, __instance, value); }
                catch (Exception) { }
            }
            HookCalls++;
            HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        /// Аниматор только что записал свои значения в картинку интерфейса (каждый кадр, пока идёт анимация).
        static void ImgAnimated(Image __instance)
        {
            if (busy || wanted.Count == 0 || __instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Noted(__instance.GetInstanceID());
            try { Handle(null, __instance, __instance.sprite); }
            catch (Exception) { }
            HookCalls++;
            HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }
}
