// ============================================================================
//  RecolorEngine.cs — перекраска сцен по правилам из папок Recolor паков Stitchwork.
//
//  Файл Recolor\<имя сцены>.txt действует только в своей сцене. Правило «#старый -> #новый»
//  находит на сцене спрайты (или частицы) со старым цветом и ставит новый. Строка «ambient:»
//  меняет общий свет сцены. Родные цвета запоминаются и возвращаются при выключении пака.
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Exuvia
{
    internal static class RecolorEngine
    {
        const float CondEvery = 0.5f;
        const float ApplyEvery = 2.0f; // как часто искать объекты, появившиеся после загрузки сцены

        sealed class SrRec { internal SpriteRenderer Sr; internal Color Vanilla; }
        sealed class PsRec { internal ParticleSystem Ps; internal Color Vanilla; }

        /// Выключатель из настроек.
        internal static bool Enabled = true;

        static SceneColors rules = new SceneColors();
        static string state = "";
        static readonly Dictionary<int, SrRec> appliedSr = new Dictionary<int, SrRec>();
        static readonly Dictionary<int, PsRec> appliedPs = new Dictionary<int, PsRec>();
        static bool ambientApplied;
        static Color ambLight, ambSky, ambEquator, ambGround;
        static float nextCond, nextApply;

        internal static int AppliedCount { get { return appliedSr.Count + appliedPs.Count; } }

        internal static void Request()
        {
            nextCond = 0f;
            nextApply = 0f;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            try
            {
                if (now >= nextCond)
                {
                    nextCond = now + CondEvery;
                    string scene = "";
                    try { scene = SceneManager.GetActiveScene().name ?? ""; }
                    catch (Exception) { }
                    CondCtx ctx = SpriteEngine.MakeCtx();
                    string st = Enabled ? StitchStore.State(ctx) + "|" + scene : "off";
                    if (st != state)
                    {
                        state = st;
                        RestoreAll();
                        rules = Enabled ? StitchStore.BuildSceneColors(ctx, scene) : new SceneColors();
                        nextApply = 0f;
                        if (!rules.Empty)
                            Plugin.Log.LogInfo("Exuvia: перекраска сцены " + scene + ": правил для спрайтов "
                                + rules.Renderer.Count
                                + ", для частиц " + rules.Particle.Count
                                    + (rules.Ambient != null ? ", общий свет" : ""));
                    }
                }
                if (rules.Empty) return;
                if (rules.Ambient != null) EnforceAmbient();
                if (now >= nextApply)
                {
                    nextApply = now + ApplyEvery;
                    Apply();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при перекраске сцены: " + e);
            }
        }

        static void Apply()
        {
            int painted = 0;
            if (rules.Renderer.Count > 0)
            {
                SpriteRenderer[] all = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    SpriteRenderer renderer = all[i];
                    if (renderer == null || !SceneOwned(renderer.gameObject)) continue;
                    int id = renderer.GetInstanceID();
                    if (appliedSr.ContainsKey(id)) continue;
                    Color tint = renderer.color;
                    ColorRule rule = Match(rules.Renderer, tint);
                    if (rule == null) continue;
                    if (rules.Groups.Count > 0 && !InGroups(renderer.transform)) continue;
                    var rec = new SrRec();
                    rec.Sr = renderer;
                    rec.Vanilla = tint;
                    appliedSr[id] = rec;
                    renderer.color = ToColor(rule.New);
                    painted++;
                }
            }
            if (rules.Particle.Count > 0)
            {
                ParticleSystem[] all = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    ParticleSystem particles = all[i];
                    if (particles == null || !SceneOwned(particles.gameObject)) continue;
                    int id = particles.GetInstanceID();
                    if (appliedPs.ContainsKey(id)) continue;
                    ParticleSystem.MainModule main = particles.main;
                    ParticleSystem.MinMaxGradient start = main.startColor;
                    if (start.mode != ParticleSystemGradientMode.Color) continue;
                    Color tint = start.color;
                    ColorRule rule = Match(rules.Particle, tint);
                    if (rule == null) continue;
                    var rec = new PsRec();
                    rec.Ps = particles;
                    rec.Vanilla = tint;
                    appliedPs[id] = rec;
                    main.startColor = new ParticleSystem.MinMaxGradient(ToColor(rule.New));
                    painted++;
                }
            }
            if (painted > 0) Plugin.Log.LogInfo("Exuvia: перекрашено объектов сцены: " + painted);
        }

        static ColorRule Match(List<ColorRule> list, Color color)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Matches(color.r, color.g, color.b, color.a)) return list[i];
            return null;
        }

        static Color ToColor(float[] c)
        {
            return new Color(c[0], c[1], c[2], c[3]);
        }

        /// Объекты, которые живут между сценами (Хорнет, интерфейс), правилами сцены не красим.
        static bool SceneOwned(GameObject obj)
        {
            return !string.Equals(obj.scene.name, "DontDestroyOnLoad", StringComparison.Ordinal);
        }

        static bool InGroups(Transform node)
        {
            string path = node.name;
            for (Transform child = node.parent; child != null; child = child.parent) path = child.name + "/" + path;
            foreach (string g in rules.Groups)
                if (path.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---- общий свет --------------------------------------------------------

        static void EnforceAmbient()
        {
            Color want = ToColor(rules.Ambient);
            if (ambientApplied && RenderSettings.ambientLight == want) return;
            // Игра поставила свой свет (или мы ещё не ставили) — запоминаем его как родной и ставим наш.
            ambLight = RenderSettings.ambientLight;
            ambSky = RenderSettings.ambientSkyColor;
            ambEquator = RenderSettings.ambientEquatorColor;
            ambGround = RenderSettings.ambientGroundColor;
            SetAmbient(want, want, want, want);
            ambientApplied = true;
        }

        static void SetAmbient(Color light, Color sky, Color equator, Color ground)
        {
            RenderSettings.ambientLight = light;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            Shader.SetGlobalColor("unity_AmbientSky", sky);
            Shader.SetGlobalColor("unity_AmbientEquator", equator);
            Shader.SetGlobalColor("unity_AmbientGround", ground);
        }

        static void RestoreAll()
        {
            foreach (SrRec rec in appliedSr.Values) if (rec.Sr != null) rec.Sr.color = rec.Vanilla;
            appliedSr.Clear();
            foreach (PsRec rec in appliedPs.Values)
            {
                if (rec.Ps == null) continue;
                ParticleSystem.MainModule main = rec.Ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(rec.Vanilla);
            }
            appliedPs.Clear();
            if (ambientApplied)
            {
                ambientApplied = false;
                // Если сцена уже сменилась, свет в ней новый — старый возвращать не нужно.
                if (rules.Ambient != null && RenderSettings.ambientLight == ToColor(rules.Ambient))
                    SetAmbient(ambLight, ambSky, ambEquator, ambGround);
            }
        }
    }
}
