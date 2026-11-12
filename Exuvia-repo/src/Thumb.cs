// ============================================================================
//  Thumb.cs — маленькая картинка-превью скина (без Unity, проверяется отдельно).
//
//  Если у скина нет своей картинки (preview.png / icon.png), превью делается из
//  его же файлов:
//   • скин Customizer: одна стоящая Хорнет с листа Knight\atlas0.png. Скины этого формата —
//     перекрашенные листы игры, поэтому она у всех на одном и том же месте (HornetRect).
//     Нет такого листа — самая большая картинка из Texture2D целиком; иначе кусок первого
//     попавшегося листа, где больше всего нарисовано;
//   • скин Stitchwork: спрайт Хорнет, в имени которого есть «idle», иначе первый.
//  Работа идёт в фоновом потоке (Preview.cs), результат — пиксели RGBA, строки
//  снизу вверх (как их хранит Unity).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;

namespace Exuvia
{
    internal static class Thumb
    {
        /// Файл, из которого делать превью (null — не из чего). crop — брать кусок, а не всю картинку.
        internal static string PickSource(string dir, bool stitch, out bool crop)
        {
            crop = false;
            try
            {
                return stitch ? PickSprite(dir) : PickAtlas(dir, out crop);
            }
            catch (Exception) { return null; }
        }

        static string PickAtlas(string dir, out bool crop)
        {
            crop = true;
            string knight = Path.Combine(Path.Combine(dir, "Knight"), "atlas0.png");
            if (File.Exists(knight)) return knight;

            string best = null;
            long bestLen = 0;
            string t2d = Path.Combine(dir, "Texture2D");
            if (Directory.Exists(t2d))
                foreach (FileRec rec in FileLister.List(t2d, ".png"))
                    if (rec.Len > bestLen) { best = rec.Path; bestLen = rec.Len; }
            if (best != null) { crop = false; return best; }

            string[] subs = Directory.GetDirectories(dir);
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (string sub in subs)
            {
                string atlas = Path.Combine(sub, "atlas0.png");
                if (File.Exists(atlas)) return atlas;
            }
            return null;
        }

        static string PickSprite(string dir)
        {
            var roots = new List<string>();
            roots.Add(Path.Combine(Path.Combine(dir, "Sprites"), "Knight"));
            string sets = Path.Combine(dir, "Sets");
            if (Directory.Exists(sets))
            {
                string[] names = Directory.GetDirectories(sets);
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                foreach (string set in names) roots.Add(Path.Combine(Path.Combine(set, "Sprites"), "Knight"));
            }
            string first = null;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] atlases = Directory.GetDirectories(root);
                Array.Sort(atlases, StringComparer.OrdinalIgnoreCase);
                foreach (string atlas in atlases)
                {
                    string[] files = Directory.GetFiles(atlas, "*.png");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string file in files)
                    {
                        if (first == null) first = file;
                        if (Path.GetFileName(file).IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) return file;
                    }
                }
                if (first != null) return first;
            }
            return first;
        }

        /// Где на листе Knight\atlas0.png (4096 x 4096, отсчёт от левого верхнего угла) стоит Хорнет:
        /// x, y, ширина, высота. Проверено на четырёх скинах (FieryHornet, VoidHornet, SteelSong, Amachan).
        static readonly int[] HornetRect = { 2829, 1326, 180, 221 };
        const int HornetSheetSize = 4096;
        const int HornetMargin = 24; // запас вокруг: у скинов спрайт бывает чуть шире родного

        /// Это лист самой Хорнет (Knight\atlas0.png)?
        internal static bool IsHornetSheet(string path)
        {
            string p = (path ?? "").Replace('\\', '/');
            return p.EndsWith("/Knight/atlas0.png", StringComparison.OrdinalIgnoreCase);
        }

        /// Делает превью не больше size x size. Возвращает пиксели RGBA (строки снизу вверх) или null.
        /// hornetSheet — это лист Knight\atlas0.png: берём с него одну стоящую Хорнет.
        internal static unsafe byte[] Make(string path, bool crop, int size, out int outW, out int outH, out string problem,
            bool hornetSheet = false)
        {
            outW = outH = 0;
            int len;
            byte[] file = Png.ReadFile(path, out len);
            PngHead head = Png.Parse(file, len);
            if (head.Problem != null) { problem = head.Problem; return null; }
            if (!head.Supported) { problem = "необычный вид PNG"; return null; }
            int w = head.Width, h = head.Height;
            if (w <= 0 || h <= 0 || (long)w * h > 8192L * 8192L) { problem = "неподходящий размер"; return null; }

            byte[] pixels = new byte[(long)w * h * 4];
            fixed (byte* p = pixels)
            {
                problem = Png.Decode(file, head, p, pixels.Length);
                if (problem != null) return null;

                int x0 = 0, y0 = 0, cw = w, ch = h;
                bool one = crop && hornetSheet && w == h && OneHornet(p, w, h, out x0, out y0, out cw, out ch);
                if (crop && !one) BusiestCell(p, w, h, out x0, out y0, out cw, out ch);
                return Shrink(p, w, x0, y0, cw, ch, size, out outW, out outH);
            }
        }

        /// Вырезает одну Хорнет с известного места листа. false — там пусто (лист устроен иначе).
        static unsafe bool OneHornet(byte* p, int w, int h, out int x0, out int y0, out int cw, out int ch)
        {
            double scale = (double)w / HornetSheetSize;
            int left = Math.Max(0, (int)((HornetRect[0] - HornetMargin) * scale));
            int top = Math.Max(0, (int)((HornetRect[1] - HornetMargin) * scale));
            int right = Math.Min(w, (int)((HornetRect[0] + HornetRect[2] + HornetMargin) * scale));
            int bottom = Math.Min(h, (int)((HornetRect[1] + HornetRect[3] + HornetMargin) * scale));
            // строки в памяти идут снизу вверх
            x0 = left; cw = right - left;
            y0 = h - bottom; ch = bottom - top;
            if (cw < 8 || ch < 8) return false;

            // Оставляем одну фигуру: всё, что связано с точкой рисунка, ближайшей к середине окна
            // (в запас вокруг попадают краешки соседних спрайтов — их стираем).
            var solid = new bool[cw * ch];
            long filled = 0;
            int seed = -1;
            long seedDist = long.MaxValue;
            for (int y = 0; y < ch; y++)
            {
                byte* row = p + ((long)(y0 + y) * w + x0) * 4;
                for (int x = 0; x < cw; x++)
                {
                    if (row[x * 4 + 3] <= 32) continue;
                    solid[y * cw + x] = true;
                    filled++;
                    long dx = x - cw / 2, dy = y - ch / 2, dist = dx * dx + dy * dy;
                    if (dist < seedDist) { seedDist = dist; seed = y * cw + x; }
                }
            }
            if (seed < 0 || filled < (long)cw * ch / 20) return false;

            var keep = new bool[cw * ch];
            var stack = new Stack<int>();
            keep[seed] = true;
            stack.Push(seed);
            int minX = cw, maxX = -1, minY = ch, maxY = -1;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int ax = at % cw, ay = at / cw;
                if (ax < minX) minX = ax;
                if (ax > maxX) maxX = ax;
                if (ay < minY) minY = ay;
                if (ay > maxY) maxY = ay;
                // соседи в двух точках вокруг: тонкие разрывы внутри фигуры не делят её на части
                for (int ny = Math.Max(0, ay - 2); ny <= Math.Min(ch - 1, ay + 2); ny++)
                    for (int nx = Math.Max(0, ax - 2); nx <= Math.Min(cw - 1, ax + 2); nx++)
                    {
                        int n = ny * cw + nx;
                        if (!solid[n] || keep[n]) continue;
                        keep[n] = true;
                        stack.Push(n);
                    }
            }
            for (int y = 0; y < ch; y++)
            {
                byte* row = p + ((long)(y0 + y) * w + x0) * 4;
                for (int x = 0; x < cw; x++)
                    if (!keep[y * cw + x]) row[x * 4 + 3] = 0;
            }
            x0 += minX; y0 += minY;
            cw = maxX - minX + 1; ch = maxY - minY + 1;
            return cw >= 8 && ch >= 8;
        }

        /// Делит картинку на клетки и выбирает ту, где больше всего непрозрачного.
        static unsafe void BusiestCell(byte* p, int w, int h, out int x0, out int y0, out int cw, out int ch)
        {
            // клетки примерно по 768 точек: на превью помещается несколько спрайтов, и они не слишком мелкие
            int nx = Math.Max(1, Math.Min(w, h) / 768);
            int ny = nx;
            cw = w / nx; ch = h / ny;
            x0 = 0; y0 = 0;
            long best = -1;
            for (int cy = 0; cy < ny; cy++)
                for (int cx = 0; cx < nx; cx++)
                {
                    long filled = 0;
                    for (int y = cy * ch; y < (cy + 1) * ch; y += 8)
                    {
                        byte* row = p + ((long)y * w + cx * cw) * 4;
                        for (int x = 0; x < cw; x += 8)
                            if (row[x * 4 + 3] > 32) filled++;
                    }
                    if (filled > best) { best = filled; x0 = cx * cw; y0 = cy * ch; }
                }
        }

        /// Уменьшает кусок картинки усреднением (с учётом прозрачности, чтобы не было тёмной каймы).
        static unsafe byte[] Shrink(byte* p, int stride, int x0, int y0, int cw, int ch, int size, out int outW, out int outH)
        {
            int k = 1;
            while (cw / k > size || ch / k > size) k++;
            outW = Math.Max(1, cw / k);
            outH = Math.Max(1, ch / k);
            byte[] result = new byte[outW * outH * 4];
            for (int oy = 0; oy < outH; oy++)
                for (int ox = 0; ox < outW; ox++)
                {
                    long r = 0, g = 0, b = 0, a = 0;
                    for (int dy = 0; dy < k; dy++)
                    {
                        byte* row = p + ((long)(y0 + oy * k + dy) * stride + x0 + ox * k) * 4;
                        for (int dx = 0; dx < k; dx++)
                        {
                            int alpha = row[dx * 4 + 3];
                            r += row[dx * 4] * alpha;
                            g += row[dx * 4 + 1] * alpha;
                            b += row[dx * 4 + 2] * alpha;
                            a += alpha;
                        }
                    }
                    int at = (oy * outW + ox) * 4;
                    if (a > 0)
                    {
                        result[at] = (byte)(r / a);
                        result[at + 1] = (byte)(g / a);
                        result[at + 2] = (byte)(b / a);
                        result[at + 3] = (byte)(a / (k * k));
                    }
                }
            return result;
        }
    }
}
