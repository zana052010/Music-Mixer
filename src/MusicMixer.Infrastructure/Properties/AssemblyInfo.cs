// Доступ к internal-типам Infrastructure из тестового проекта.
// Нужен только для проверки примитивов DSP (FFT, детекторы), поэтому
// публичным API эти типы не становятся.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MusicMixer.Tests")]
