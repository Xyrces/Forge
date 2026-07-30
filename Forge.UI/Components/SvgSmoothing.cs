using System.Text;

namespace Forge.Dashboard.Components;

/// <summary>
/// SVG path smoothing shared by the Flow page and the dashboard's
/// compact strip: Catmull-Rom → cubic Bézier so the layout's routed
/// polylines read as arcs.
/// </summary>
public static class SvgSmoothing
{
    public static string SmoothPath(IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count == 0) return "";
        if (pts.Count < 3)
        {
            return $"M {F(pts[0].X)} {F(pts[0].Y)} " +
                string.Join(" ", pts.Skip(1).Select(p => $"L {F(p.X)} {F(p.Y)}"));
        }
        var sb = new StringBuilder($"M {F(pts[0].X)} {F(pts[0].Y)}");
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            sb.Append($" C {F(p1.X + (p2.X - p0.X) / 6.0)} {F(p1.Y + (p2.Y - p0.Y) / 6.0)}," +
                      $" {F(p2.X - (p3.X - p1.X) / 6.0)} {F(p2.Y - (p3.Y - p1.Y) / 6.0)}," +
                      $" {F(p2.X)} {F(p2.Y)}");
        }
        return sb.ToString();
    }

    public static string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
