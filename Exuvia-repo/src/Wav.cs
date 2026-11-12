// ============================================================================
//  Wav.cs — чтение WAV-файла в числа (без Unity, проверяется отдельно).
//  Понимает обычный PCM 8/16/24/32 бита и 32-битный с плавающей точкой.
// ============================================================================
using System;

namespace Exuvia
{
    internal static class Wav
    {
        /// Разбирает WAV. samples — отсчёты подряд по каналам, от -1 до 1. Возвращает текст ошибки или null.
        internal static string Parse(byte[] b, out float[] samples, out int channels, out int rate)
        {
            samples = null;
            channels = 0;
            rate = 0;
            if (b == null || b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W'
                || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                return "это не WAV";

            int format = 0, bits = 0, dataAt = -1, dataLen = 0;
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = "" + (char)b[pos] + (char)b[pos + 1] + (char)b[pos + 2] + (char)b[pos + 3];
                long len = BitConverter.ToUInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && body + 16 <= b.Length)
                {
                    format = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    // «расширенный» формат: настоящий вид данных записан дальше
                    if (format == 0xFFFE && len >= 26 && body + 26 <= b.Length)
                        format = BitConverter.ToUInt16(b, body + 24);
                }
                else if (id == "data")
                {
                    dataAt = body;
                    dataLen = (int)Math.Min(len, (long)b.Length - body);
                    break;
                }
                long next = body + len + (len & 1); // куски выравниваются по чётной границе
                if (next <= pos || next > int.MaxValue) break;
                pos = (int)next;
            }

            if (dataAt < 0 || channels <= 0 || rate <= 0) return "в WAV нет звуковых данных";
            if (format != 1 && format != 3)
                return "сжатый WAV (вид " + format + ") не поддерживается, нужен обычный PCM";
            int bytesPer = bits / 8;
            if (bytesPer < 1 || bytesPer > 4 || (format == 3 && bits != 32))
                return "WAV с разрядностью " + bits + " бит не поддерживается";

            int count = dataLen / bytesPer;
            count -= count % channels;
            var s = new float[count];
            int p = dataAt;
            for (int i = 0; i < count; i++, p += bytesPer)
            {
                if (format == 3) s[i] = BitConverter.ToSingle(b, p);
                else if (bytesPer == 1) s[i] = (b[p] - 128) / 128f;
                else if (bytesPer == 2) s[i] = (short)(b[p] | (b[p + 1] << 8)) / 32768f;
                else if (bytesPer == 3) s[i] = (((sbyte)b[p + 2] << 16) | (b[p + 1] << 8) | b[p]) / 8388608f;
                else s[i] = BitConverter.ToInt32(b, p) / 2147483648f;
            }
            samples = s;
            return null;
        }
    }
}
