using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace IptvPlayer.Controls;


public static class AppIcons
{

    public static readonly Brush RecordRed = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x3B, 0x3B));

    private static readonly Brush White = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));


    private static Brush? _lightFgBrush;

    private static Brush Fg()
    {
        // Cache the light-theme brush — allocating per call is wasteful
        if ((MainWindow.Instance?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Light)
        {
            return _lightFgBrush ??= new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00));
        }

        return White;
    }


    public static Ellipse RecordDot(double size = 14)
    {
        return new Ellipse
        {
            Width = size,
            Height = size,
            Fill = RecordRed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }


    public static Rectangle StopSquare(double size = 13)
    {
        return new Rectangle
        {
            Width = size,
            Height = size,
            RadiusX = 2,
            RadiusY = 2,
            Fill = RecordRed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }


    public static Microsoft.UI.Xaml.Shapes.Path Play(double size = 16)
    {


        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(4, 2), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(13, 8) });
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(4, 14) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Fill = Fg(),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }


    public static Grid Pause(double size = 16)
    {
        var barWidth = size * 0.22;
        var grid = new Grid { Width = size, Height = size };
        foreach (var leftMargin in new[] { 0.0, size - barWidth })
        {
            grid.Children.Add(new Rectangle
            {
                Width = barWidth,
                Height = size,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Fg(),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(leftMargin, 0, 0, 0)
            });
        }
        return grid;
    }


    public static Grid SpeakerOn(double size = 16)
    {
        var grid = new Grid { Width = size, Height = size };

        var body = new PathGeometry();
        body.Figures.Add(PolygonFigure(
            new Windows.Foundation.Point(1.5, 5.6),
            new Windows.Foundation.Point(4.1, 5.6),
            new Windows.Foundation.Point(8.6, 1.9),
            new Windows.Foundation.Point(8.6, 14.1),
            new Windows.Foundation.Point(4.1, 10.4),
            new Windows.Foundation.Point(1.5, 10.4)));
        grid.Children.Add(IconPath(body, stroke: false));

        var waves = new PathGeometry();
        waves.Figures.Add(ArcFigure(new(10.8, 5.6), new(10.8, 10.4), 3.3));
        waves.Figures.Add(ArcFigure(new(12.8, 3.6), new(12.8, 12.4), 6.1));
        grid.Children.Add(IconPath(waves, stroke: true));

        return grid;
    }


    public static Grid SpeakerMuted(double size = 16)
    {
        var grid = new Grid { Width = size, Height = size };

        var body = new PathGeometry();
        body.Figures.Add(PolygonFigure(
            new Windows.Foundation.Point(1.5, 5.6),
            new Windows.Foundation.Point(4.1, 5.6),
            new Windows.Foundation.Point(8.6, 1.9),
            new Windows.Foundation.Point(8.6, 14.1),
            new Windows.Foundation.Point(4.1, 10.4),
            new Windows.Foundation.Point(1.5, 10.4)));
        grid.Children.Add(IconPath(body, stroke: false));

        var cross = new PathGeometry();
        cross.Figures.Add(PolylineFigure(new(10.7, 5.9), new(14.3, 10.1)));
        cross.Figures.Add(PolylineFigure(new(14.3, 5.9), new(10.7, 10.1)));
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = cross,
            Stroke = Fg(),
            StrokeThickness = 1.5,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return grid;
    }

    private static Microsoft.UI.Xaml.Shapes.Path IconPath(PathGeometry geometry, bool stroke)
    {
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Fill = stroke ? null : Fg(),
            Stroke = stroke ? Fg() : null,
            StrokeThickness = 1.3,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static PathFigure PolygonFigure(params Windows.Foundation.Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
        for (var i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment { Point = points[i] });
        }
        return figure;
    }

    private static PathFigure PolylineFigure(params Windows.Foundation.Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
        for (var i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment { Point = points[i] });
        }
        return figure;
    }

    private static PathFigure ArcFigure(
        Windows.Foundation.Point start, Windows.Foundation.Point end, double radius)
    {
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Windows.Foundation.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        });
        return figure;
    }
}
