// ============================================================================
//  Spare.cs — общий запас видеопамяти под недавно использованные скины.
//
//  Чтобы повторное включение скина было мгновенным, мод не выбрасывает готовое сразу:
//    • атласы выключенных Customizer-скинов (Engine),
//    • готовые картинки меню и интерфейса (T2D),
//    • готовые листы спрайтов выключенных Stitchwork-скинов (SpriteEngine).
//  У всех трёх один общий предел (BudgetMb). Когда он превышен, убирается то, что дольше
//  всего не было нужно, — неважно, какого оно вида. «Давность» — общий счётчик Next().
//  Что менять: предел — настройка General/KeepRecentMB (см. Plugin.cs).
// ============================================================================
namespace Exuvia
{
    internal static class Spare
    {
        /// Сколько МБ видеопамяти можно держать про запас (0 — ничего не держать).
        internal static int BudgetMb = 1024;

        static long stamp;

        /// Отметка «использовано только что» (больше — новее).
        internal static long Next()
        {
            return ++stamp;
        }

        /// Сколько сейчас лежит про запас, МБ.
        internal static long TotalMb
        {
            get { return (Engine.SpareBytes + T2D.SpareBytes + SpriteEngine.SpareBytes) / (1024 * 1024); }
        }

        /// Убирает самое давнее, пока запас не уложится в предел.
        internal static void Trim()
        {
            long budget = (long)System.Math.Max(0, BudgetMb) * 1024 * 1024;
            Engine.RefreshIdle();
            int atlases = 0, pictures = 0, sheets = 0;
            for (int guard = 0; guard < 100000; guard++)
            {
                long total = Engine.SpareBytes + T2D.SpareBytes + SpriteEngine.SpareBytes;
                if (total <= budget) break;
                long a = Engine.OldestSpare, b = T2D.OldestSpare, c = SpriteEngine.OldestSpare;
                if (a != long.MaxValue && a <= b && a <= c) { if (!Engine.EvictOldestSpare()) break; atlases++; }
                else if (b != long.MaxValue && b <= c) { if (!T2D.EvictOldestSpare()) break; pictures++; }
                else if (c != long.MaxValue) { if (!SpriteEngine.EvictOldestSpare()) break; sheets++; }
                else break;
            }
            if (atlases + pictures + sheets > 0)
                Plugin.Log.LogInfo("Exuvia: запас недавних скинов превысил " + BudgetMb
                    + " МБ — убрано давно не нужное: атласов " + atlases
                    + ", картинок меню и интерфейса " + pictures + ", наборов листов спрайтов " + sheets
                        + "; осталось про запас " + TotalMb + " МБ.");
        }
    }
}
