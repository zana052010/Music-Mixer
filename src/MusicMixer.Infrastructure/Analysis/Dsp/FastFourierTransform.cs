// Инфраструктурный слой: собственная radix-2 FFT для анализа аудио.
// Вызывается из TempoDetector и KeyDetector вместо внешней DSP-библиотеки.

namespace MusicMixer.Infrastructure.Analysis.Dsp;

/// <summary>
/// Small in-place radix-2 FFT. Avoiding an extra DSP package keeps the audio
/// analysis dependency-free and predictable across desktop platforms.
/// </summary>
internal static class FastFourierTransform
{
    /// <summary>
    /// Выполняет прямое преобразование Фурье на месте: массивы содержат действительную и мнимую части.
    /// Длина должна быть степенью двойки, результат остаётся в тех же массивах в порядке частотных бинов.
    /// </summary>
    public static void Transform(double[] real, double[] imaginary)
    {
        if (real.Length != imaginary.Length)
        {
            throw new ArgumentException("Real and imaginary buffers must be the same length.");
        }

        var length = real.Length;
        if (length < 2 || (length & (length - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two greater than one.", nameof(real));
        }

        // Переставляем элементы в порядке битовой инверсии индексов: это обязательный
        // первый шаг radix-2, после которого баттерфляи работают с соседними парами.
        var swapIndex = 0;
        for (var i = 1; i < length; i++)
        {
            var bit = length >> 1;
            while ((swapIndex & bit) != 0 && bit > 0)
            {
                swapIndex ^= bit;
                bit >>= 1;
            }

            swapIndex ^= bit;
            if (i < swapIndex)
            {
                (real[i], real[swapIndex]) = (real[swapIndex], real[i]);
                (imaginary[i], imaginary[swapIndex]) = (imaginary[swapIndex], imaginary[i]);
            }
        }

        // Каждый проход удваивает длину блока: сначала баттерфляи по 2 точки,
        // затем по 4, 8 и так до полного массива.
        for (var blockLength = 2; blockLength <= length; blockLength <<= 1)
        {
            // Отрицательный угол соответствует прямому преобразованию; корень
            // поворота один на блок, а внутри множитель домножается на него.
            var angle = -2.0 * Math.PI / blockLength;
            var rootReal = Math.Cos(angle);
            var rootImaginary = Math.Sin(angle);
            var halfBlock = blockLength >> 1;

            for (var blockStart = 0; blockStart < length; blockStart += blockLength)
            {
                var twiddleReal = 1.0;
                var twiddleImaginary = 0.0;

                for (var offset = 0; offset < halfBlock; offset++)
                {
                    var evenIndex = blockStart + offset;
                    var oddIndex = evenIndex + halfBlock;
                    var oddReal = (real[oddIndex] * twiddleReal) - (imaginary[oddIndex] * twiddleImaginary);
                    var oddImaginary = (real[oddIndex] * twiddleImaginary) + (imaginary[oddIndex] * twiddleReal);

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextTwiddleReal = (twiddleReal * rootReal) - (twiddleImaginary * rootImaginary);
                    twiddleImaginary = (twiddleReal * rootImaginary) + (twiddleImaginary * rootReal);
                    twiddleReal = nextTwiddleReal;
                }
            }
        }
    }

    /// <summary>
    /// Строит окно Ханна для сглаживания краёв кадра перед FFT: так меньше утечки спектра.
    /// </summary>
    public static double[] CreateHannWindow(int length)
    {
        var window = new double[length];
        for (var index = 0; index < length; index++)
        {
            // Знаменатель length - 1 делает окно симметричным с нулями на обоих концах.
            window[index] = 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * index) / (length - 1)));
        }

        return window;
    }
}
