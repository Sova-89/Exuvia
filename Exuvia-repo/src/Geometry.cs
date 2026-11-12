// ============================================================================
//  Geometry.cs — чистая математика подмены спрайтов (без Unity, проверяется отдельно):
//   • Packer — раскладка маленьких PNG по листам-атласам;
//   • SpriteGeometry — новые углы четырёхугольника спрайта и его uv.
// ============================================================================
using System;
using System.Collections.Generic;

namespace Exuvia
{
    internal struct P2
    {
        internal float X, Y;
        internal P2(float x, float y) { X = x; Y = y; }
    }

    internal static class Packer
    {
        internal sealed class Item
        {
            internal int Index; // номер входной картинки
            internal int Page;
            internal int X, Y;  // левый нижний угол картинки на листе
        }

        internal sealed class Page
        {
            internal int W, H;
        }

        internal sealed class Result
        {
            internal readonly List<Page> Pages = new List<Page>();
            internal readonly List<Item> Items = new List<Item>();
            internal readonly List<int> TooBig = new List<int>(); // не поместились даже на пустой лист
        }

        /// Раскладка «полками». pad — зазор между картинками (чтобы при сглаживании цвет не затекал с соседей).
        internal static Result Pack(int[] ws, int[] hs, int maxSize, int pad)
        {
            int n = ws.Length;
            var res = new Result();
            var order = new List<int>();
            long area = 0;
            int maxW = 0;
            for (int i = 0; i < n; i++)
            {
                if (ws[i] + pad > maxSize || hs[i] + pad > maxSize) { res.TooBig.Add(i); continue; }
                order.Add(i);
                area += (long)(ws[i] + pad) * (hs[i] + pad);
                if (ws[i] + pad > maxW) maxW = ws[i] + pad;
            }
            if (order.Count == 0) return res;

            order.Sort(delegate (int a, int b)
            {
                int c = hs[b].CompareTo(hs[a]);
                if (c != 0) return c;
                c = ws[b].CompareTo(ws[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            // Подбираем ширину листа: чем уже и квадратнее, тем меньше пустого места и памяти.
            int width = RoundUp4((int)Math.Ceiling(Math.Sqrt(area * 1.15)));
            if (width < maxW) width = RoundUp4(maxW);
            if (width > maxSize) width = maxSize;

            Result best = null;
            while (true)
            {
                Result result = Shelf(order, ws, hs, width, maxSize, pad);
                if (result.Pages.Count == 1 || width >= maxSize) { best = result; break; }
                width = Math.Min(maxSize, RoundUp4((int)(width * 1.25)));
            }
            best.TooBig.AddRange(res.TooBig);
            return best;
        }

        static Result Shelf(List<int> order, int[] ws, int[] hs, int width, int maxSize, int pad)
        {
            var result = new Result();
            int page = 0, x = 0, y = 0, shelfH = 0, usedH = 0;
            result.Pages.Add(new Page());

            foreach (int i in order)
            {
                int cw = ws[i] + pad, ch = hs[i] + pad;
                if (x + cw > width) { y += shelfH; x = 0; shelfH = 0; }
                if (y + ch > maxSize)
                {
                    result.Pages[page].W = width;
                    result.Pages[page].H = RoundUp4(Math.Max(usedH, 4));
                    result.Pages.Add(new Page());
                    page++; x = 0; y = 0; shelfH = 0; usedH = 0;
                }
                var item = new Item();
                item.Index = i;
                item.Page = page;
                item.X = x + pad / 2;
                item.Y = y + pad / 2;
                result.Items.Add(item);

                x += cw;
                if (ch > shelfH) shelfH = ch;
                if (y + shelfH > usedH) usedH = y + shelfH;
            }
            result.Pages[page].W = width;
            result.Pages[page].H = RoundUp4(Math.Max(usedH, 4));
            return result;
        }

        static int RoundUp4(int v)
        {
            return (v + 3) & ~3;
        }
    }

    internal static class SpriteGeometry
    {
        /// Нужна ли новая геометрия: только когда картинка из пака больше исходного спрайта
        /// (так же решает Stitchwork). Тогда спрайт рисуется в том же масштабе пикселей, но шире/выше,
        /// а якорь (@t @b @l @r @c, по умолчанию низ по центру) или точка опоры (@p<x>_<y>) говорят, куда он растёт.
        /// pos — исходные углы в мировых единицах tk2d; sw*sh — размер исходного спрайта в пикселях
        /// (уже повёрнутого как в игре); w*h — размер картинки из пака; flipped — спрайт лежит в атласе повёрнутым.
        /// Формулы точки опоры повторяют Stitchwork: y считается от нижнего края, x у повёрнутых спрайтов — зеркально.
        internal static bool Place(P2[] pos, int sw, int sh, int w, int h, char anchor, int pivotX, int pivotY,
            bool flipped, P2[] outPos, out string how)
        {
            how = "";
            if (!(w > sw || h > sh)) return false;
            if (sw <= 0 || sh <= 0) return false;

            float xmin = float.MaxValue, xmax = float.MinValue, ymin = float.MaxValue, ymax = float.MinValue;
            for (int i = 0; i < pos.Length; i++)
            {
                if (pos[i].X < xmin) xmin = pos[i].X;
                if (pos[i].X > xmax) xmax = pos[i].X;
                if (pos[i].Y < ymin) ymin = pos[i].Y;
                if (pos[i].Y > ymax) ymax = pos[i].Y;
            }
            float dx = xmax - xmin, dy = ymax - ymin;
            if (dx < 1e-6f || dy < 1e-6f) return false;

            float uppX = dx / sw, uppY = dy / sh; // мировых единиц на пиксель
            float extraW = (w - sw) * uppX, extraH = (h - sh) * uppY;
            float x0, x1, y0, y1;

            if (pivotX >= 0 && pivotY >= 0)
            {
                int px = flipped ? (w - 1 - pivotX) : pivotX;
                x0 = -px * uppX;
                x1 = (w - px) * uppX;
                y0 = -pivotY * uppY;
                y1 = (h - pivotY) * uppY;
                how = "опора " + pivotX + "," + pivotY;
            }
            else
            {
                char a = anchor == '\0' ? 'b' : anchor;
                switch (a)
                {
                    case 't': x0 = xmin - extraW * 0.5f; x1 = xmax + extraW * 0.5f; y1 = ymax; y0 =
                        ymin - extraH; break;
                    case 'l': x0 = xmin; x1 = xmax + extraW; y0 = ymin - extraH * 0.5f; y1 =
                        ymax + extraH * 0.5f; break;
                    case 'r': x1 = xmax; x0 = xmin - extraW; y0 = ymin - extraH * 0.5f; y1 =
                        ymax + extraH * 0.5f; break;
                    case 'c': x0 = xmin - extraW * 0.5f; x1 = xmax + extraW * 0.5f; y0 = ymin - extraH * 0.5f; y1 =
                        ymax + extraH * 0.5f; break;
                    // 'b'
                    default: x0 = xmin - extraW * 0.5f; x1 = xmax + extraW * 0.5f; y0 = ymin; y1 = ymax + extraH; break;
                }
                how = "якорь " + a;
            }

            float mx = (xmin + xmax) * 0.5f, my = (ymin + ymax) * 0.5f;
            for (int i = 0; i < pos.Length; i++)
                outPos[i] = new P2(pos[i].X > mx ? x1 : x0, pos[i].Y > my ? y1 : y0);
            return true;
        }

        /// Размер родного спрайта в атласе, пикселей (ширина и высота как в атласе, без учёта поворота).
        /// Округление такое же, как у Stitchwork: от этого зависит, считается ли картинка из пака «увеличенной».
        internal static void AtlasRect(P2[] uv, int texW, int texH, out int w, out int h)
        {
            float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
            for (int i = 0; i < uv.Length; i++)
            {
                if (uv[i].X < umin) umin = uv[i].X;
                if (uv[i].X > umax) umax = uv[i].X;
                if (uv[i].Y < vmin) vmin = uv[i].Y;
                if (uv[i].Y > vmax) vmax = uv[i].Y;
            }
            int x0 = (int)Math.Floor(umin * texW + 0.25f), x1 = (int)Math.Ceiling(umax * texW - 0.25f);
            int y0 = (int)Math.Floor(vmin * texH + 0.25f), y1 = (int)Math.Ceiling(vmax * texH - 0.25f);
            w = Math.Min(x1 - x0, texW);
            h = Math.Min(y1 - y0, texH);
        }

        /// uv углов для картинки на нашем листе (прямоугольник u0..u1, v0..v1).
        /// vanillaUv — родные uv спрайта: по ним видно, какой угол спрайта смотрит в какой угол его места в атласе.
        /// flip — как спрайт лежит в атласе игры: 0 прямо, 1 (Tk2d) и 2 (TPackerCW) повёрнут на бок.
        /// Картинки повёрнутых спрайтов в паках хранятся так, как их кладёт в атлас Stitchwork/Patchwork
        /// (для Tk2d: точка атласа (a,b) берётся из точки картинки (1-b, a); для TPackerCW: из (b, 1-a)).
        /// Без этого повёрнутые спрайты выходят зеркальными.
        internal static void Uvs(P2[] vanillaUv, int flip, float u0, float v0, float u1, float v1, P2[] outUv)
        {
            float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
            for (int i = 0; i < vanillaUv.Length; i++)
            {
                if (vanillaUv[i].X < umin) umin = vanillaUv[i].X;
                if (vanillaUv[i].X > umax) umax = vanillaUv[i].X;
                if (vanillaUv[i].Y < vmin) vmin = vanillaUv[i].Y;
                if (vanillaUv[i].Y > vmax) vmax = vanillaUv[i].Y;
            }
            float du = umax - umin, dv = vmax - vmin;
            for (int i = 0; i < vanillaUv.Length; i++)
            {
                float a = du > 1e-9f ? (vanillaUv[i].X - umin) / du : 0.5f; // место угла в родном прямоугольнике атласа
                float b = dv > 1e-9f ? (vanillaUv[i].Y - vmin) / dv : 0.5f;
                float p, q;                                                 // место того же угла на картинке из пака
                if (flip == 1) { p = 1f - b; q = a; }
                else if (flip == 2) { p = b; q = 1f - a; }
                else { p = a; q = b; }
                outUv[i] = new P2(u0 + p * (u1 - u0), v0 + q * (v1 - v0));
            }
        }
    }
}
