// Пользовательский элемент управления волновой формой.
// Слой Desktop: рисует дорожку, курсор, выделение для обрезки и маску
// проигранной части. Реальные пики приходят из Peaks, иначе используется
// детерминированный демонстрационный рисунок.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicMixer.Desktop.Controls;

/// <summary>
/// Лёгкая дорожка волновой формы с курсором. Умеет рисовать переданные пики
/// или детерминированный визуальный макет, пока реальных данных нет.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<float>?>(nameof(Peaks));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayheadBrush));

    public static readonly StyledProperty<int> SeedProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(Seed), 7);

    public static readonly StyledProperty<bool> ShowSelectionProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowSelection));

    public static readonly StyledProperty<double> SelectionStartProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(SelectionStart));

    public static readonly StyledProperty<double> SelectionEndProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(SelectionEnd), 1);

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(BaselineBrush));

    public static readonly StyledProperty<IBrush?> SelectionMaskBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(SelectionMaskBrush));

    public static readonly StyledProperty<IBrush?> SelectionMarkerBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(SelectionMarkerBrush));

    // Цвета по умолчанию повторяют палитру темы. Свойства контрола
    // позволяют XAML переопределить их для светлой/тёмной темы.
    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#626262"));
    private static readonly IBrush DefaultPlayheadBrush = new SolidColorBrush(Color.Parse("#0A0A0A"));
    private static readonly IBrush DefaultBaselineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IBrush DefaultSelectionMaskBrush = new SolidColorBrush(Color.Parse("#B3FFFFFF"));
    private static readonly IBrush DefaultSelectionMarkerBrush = new SolidColorBrush(Color.Parse("#0A0A0A"));

    static WaveformView()
    {
        // Любое из этих свойств меняет картинку, поэтому Avalonia должна
        // перерисовать контрол при их изменении.
        AffectsRender<WaveformView>(
            PeaksProperty,
            ProgressProperty,
            LineBrushProperty,
            PlayheadBrushProperty,
            SeedProperty,
            ShowSelectionProperty,
            SelectionStartProperty,
            SelectionEndProperty,
            BaselineBrushProperty,
            SelectionMaskBrushProperty,
            SelectionMarkerBrushProperty);
    }

    public IReadOnlyList<float>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public int Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    public bool ShowSelection
    {
        get => GetValue(ShowSelectionProperty);
        set => SetValue(ShowSelectionProperty, value);
    }

    public double SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public double SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public IBrush? SelectionMaskBrush
    {
        get => GetValue(SelectionMaskBrushProperty);
        set => SetValue(SelectionMaskBrushProperty, value);
    }

    public IBrush? SelectionMarkerBrush
    {
        get => GetValue(SelectionMarkerBrushProperty);
        set => SetValue(SelectionMarkerBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }
        var lineBrush = LineBrush ?? DefaultLineBrush;
        var playheadBrush = PlayheadBrush ?? DefaultPlayheadBrush;
        var baselineBrush = BaselineBrush ?? DefaultBaselineBrush;
        var selectionMaskBrush = SelectionMaskBrush ?? DefaultSelectionMaskBrush;
        var selectionMarkerBrush = SelectionMarkerBrush ?? playheadBrush;
        var centerY = height / 2;
        var baselinePen = new Pen { Brush = baselineBrush, Thickness = 1 };

        using (context.PushClip(new RoundedRect(new Rect(0, 0, width, height), 6)))
        {
            context.DrawLine(
                baselinePen,
                new Point(0, centerY),
                new Point(width, centerY));

            var progress = Math.Clamp(Progress, 0, 1);
            var playheadX = width * progress;
            var suppliedPeaks = Peaks is { Count: > 0 } ? Peaks : null;
            // Если реальных пиков нет, рисуем правдоподобный макет.
            // Количество столбиков зависит от ширины, но ограничено,
            // чтобы не рисовать тысячи линий на узкой панели.
            var count = suppliedPeaks?.Count ?? Math.Clamp((int)(width / 4.5), 64, 220);

            if (count > 0)
            {
                IReadOnlyList<float> peaks = suppliedPeaks ?? CreatePreviewPeaks(count, new Random(Seed));
                var pen = new Pen { Brush = lineBrush, Thickness = 1.5, LineCap = PenLineCap.Round };
                // Два прохода: сначала вся дорожка приглушённо, затем
                // проигранная часть до курсора — ярко. Так виден прогресс.
                using (context.PushOpacity(0.27))
                {
                    DrawBars(context, peaks, count, width, height, playheadX, width, pen);
                }

                DrawBars(context, peaks, count, width, height, 0, playheadX, pen);

                if (ShowSelection)
                {
                    // Выделение задаётся в долях 0..1, как и Progress.
                    // Маска затемняет всё вне диапазона, а маркеры
                    // показывают границы IN/OUT.
                    var selectionStart = Math.Clamp(SelectionStart, 0, 1) * width;
                    var selectionEnd = Math.Clamp(SelectionEnd, 0, 1) * width;
                    if (selectionStart > selectionEnd)
                    {
                        (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
                    }

                    if (selectionStart > 0)
                    {
                        context.FillRectangle(
                            selectionMaskBrush,
                            new Rect(0, 0, selectionStart, height));
                    }

                    if (selectionEnd < width)
                    {
                        context.FillRectangle(
                            selectionMaskBrush,
                            new Rect(selectionEnd, 0, width - selectionEnd, height));
                    }

                    var selectionPen = new Pen
                    {
                        Brush = selectionMarkerBrush,
                        Thickness = 1,
                    };
                    context.DrawLine(
                        selectionPen,
                        new Point(selectionStart, 0),
                        new Point(selectionStart, height));
                    context.DrawLine(
                        selectionPen,
                        new Point(selectionEnd, 0),
                        new Point(selectionEnd, height));
                }

                var playheadPen = new Pen
                {
                    Brush = playheadBrush,
                    Thickness = 1.4,
                    LineCap = PenLineCap.Round,
                };
                context.DrawLine(
                    playheadPen,
                    new Point(playheadX, 3),
                    new Point(playheadX, height - 3));

                var markerBrush = playheadBrush;
                context.DrawEllipse(markerBrush, null, new Point(playheadX, 4), 2.4, 2.4);
            }
        }
    }

    private static void DrawBars(
        DrawingContext context,
        IReadOnlyList<float> peaks,
        int count,
        double width,
        double height,
        double startX,
        double endX,
        Pen pen)
    {
        if (endX <= startX)
        {
            return;
        }

        var centerY = height / 2;
        var step = width / count;
        // Индексы с запасом в один столбик, чтобы на границе диапазона
        // не оставалось визуального разрыва.
        var firstIndex = Math.Max(0, (int)Math.Floor(startX / step) - 1);
        var lastIndex = Math.Min(count - 1, (int)Math.Ceiling(endX / step) + 1);

        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var x = (index + 0.5) * step;
            if (x < startX - step || x > endX + step)
            {
                continue;
            }

            var amplitude = Math.Clamp(peaks[index], 0.015f, 1f);
            var halfHeight = amplitude * (height * 0.44);
            context.DrawLine(
                pen,
                new Point(x, centerY - halfHeight),
                new Point(x, centerY + halfHeight));
        }
    }

    /// <summary>
    /// Детерминированный макет волны: сумма синусоид разной частоты плюс шум.
    /// Он не претендует на реальные пики, но выглядит как музыкальная дорожка
    /// и не меняется между перерисовками при том же Seed.
    /// </summary>
    private static float[] CreatePreviewPeaks(int count, Random random)
    {
        var peaks = new float[count];
        for (var index = 0; index < count; index++)
        {
            var normalized = index / (double)Math.Max(1, count - 1);
            var envelope = Math.Pow(Math.Sin(Math.PI * normalized), 0.35);
            var phrase = 0.55 + (0.45 * Math.Abs(Math.Sin((normalized * 19) + (random.NextDouble() * 1.4))));
            var pulse = 0.45 + (0.55 * Math.Abs(Math.Sin(normalized * 91 + 0.3)));
            var noise = 0.35 + (0.65 * random.NextDouble());
            peaks[index] = (float)Math.Clamp(
                0.06 + (0.86 * envelope * phrase * pulse * noise),
                0.02,
                1);
        }

        return peaks;
    }
}
