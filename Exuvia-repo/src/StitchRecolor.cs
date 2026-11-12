// ============================================================================
//  StitchRecolor.cs — правила перекраски сцен (чтение файлов Recolor).
//  Раньше было частью Stitch.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    /// Правило перекраски: объекты сцены с цветом Old получают цвет New.
    internal sealed class ColorRule
    {
        internal readonly float[] Old = new float[4]; // r g b a, от 0 до 1
        internal readonly float[] New = new float[4];
        internal float Tol = 0.05f;                   // допуск на каждый канал
        internal bool Particle;                       // правило для частиц, а не для спрайтов

        internal bool Matches(float r, float g, float b, float a)
        {
            return Math.Abs(r - Old[0]) <= Tol && Math.Abs(g - Old[1]) <= Tol && Math.Abs(b - Old[2]) <= Tol
                && Math.Abs(a - Old[3]) <= Tol;
        }
    }

    /// Все правила перекраски одной сцены.
    internal sealed class SceneColors
    {
        internal readonly List<ColorRule> Renderer = new List<ColorRule>();
        internal readonly List<ColorRule> Particle = new List<ColorRule>();
        // если не пусто — красим только объекты, в пути которых есть одно из этих имён
        internal readonly List<string> Groups = new List<string>();
        internal float[] Ambient;                                   // общий свет сцены, null — не менять
        internal bool Empty { get { return Renderer.Count == 0 && Particle.Count == 0 && Ambient == null; } }
    }

    /// Файлы перекраски: Recolor\<имя сцены>.txt, строки вида  #RRGGBBAA -> #RRGGBBAA [допуск],
    /// «particle: …» для частиц, «group: имя» и «ambient: #RRGGBB».
    internal static class StitchRecolor
    {
        internal static bool TryParseHex(string s, float[] c)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            uint v;
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            if (s.Length == 6) v = (v << 8) | 255u;
            c[0] = ((v >> 24) & 255u) / 255f;
            c[1] = ((v >> 16) & 255u) / 255f;
            c[2] = ((v >> 8) & 255u) / 255f;
            c[3] = (v & 255u) / 255f;
            return true;
        }

        internal static bool TryParseRule(string line, out ColorRule rule)
        {
            rule = null;
            line = line.Trim();
            bool particle = false;
            if (line.StartsWith("particle:", StringComparison.OrdinalIgnoreCase))
            {
                particle = true;
                line = line.Substring(9).Trim();
            }
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) return false;
            string left = line.Substring(0, arrow).Trim();
            string right = line.Substring(arrow + 2).Trim();

            var colorRule = new ColorRule();
            colorRule.Particle = particle;
            int space = right.IndexOf(' ');
            if (space > 0)
            {
                float tol;
                if (float.TryParse(right.Substring(space + 1).Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out tol))
                    colorRule.Tol = Math.Max(0.001f, Math.Min(0.5f, tol));
                right = right.Substring(0, space).Trim();
            }
            if (!TryParseHex(left, colorRule.Old) || !TryParseHex(right, colorRule.New)) return false;
            rule = colorRule;
            return true;
        }

        /// Дочитывает файл в набор правил. Правило на тот же исходный цвет заменяет прежнее (поздний файл важнее).
        internal static void LoadFile(string file, SceneColors into)
        {
            if (!File.Exists(file)) return;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                {
                    string g = line.Substring(6).Trim();
                    if (g.Length > 0 && !into.Groups.Contains(g)) into.Groups.Add(g);
                    continue;
                }
                if (line.StartsWith("ambient:", StringComparison.OrdinalIgnoreCase))
                {
                    var c = new float[4];
                    if (TryParseHex(line.Substring(8).Trim(), c)) into.Ambient = c;
                    continue;
                }
                ColorRule rule;
                if (!TryParseRule(line, out rule)) continue;
                List<ColorRule> list = rule.Particle ? into.Particle : into.Renderer;
                int same = -1;
                for (int i = 0; i < list.Count; i++)
                    if (Close(list[i].Old, rule.Old)) { same = i; break; }
                if (same >= 0) list[same] = rule; else list.Add(rule);
            }
        }

        static bool Close(float[] a, float[] b)
        {
            for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > 0.002f) return false;
            return true;
        }
    }
}
