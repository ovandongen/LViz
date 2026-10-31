using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;

namespace LViz.Core.Layout;

/// <summary>
/// Pure geometry over <see cref="UserLayoutKey"/>s for the layout editor:
/// rotated centres and corners, hit-testing, snap steps, rotation
/// normalisation, and the move/rotate/hand/normalise operations that produce
/// new key lists. All values are ZMK units (centi-u, centi-degrees); rotation
/// follows <see cref="KeyPosition.RotatedBounds"/>
/// (<c>x' = px + dx·cos − dy·sin</c>, <c>y' = py + dx·sin + dy·cos</c> with y
/// pointing down, so positive angles are clockwise on screen). A key rotates
/// about its pivot <c>(Rx, Ry)</c> only when <c>R ≠ 0</c> — an unrotated key's
/// pivot is ignored, mirroring <c>DtsiBackedLayoutBuilder</c>.
/// </summary>
public static class UserLayoutGeometry
{
    /// <summary>Positions snap in 0.25u steps (centi-u).</summary>
    public const int PositionStep = 25;

    /// <summary>Rotations snap in 5° steps (centi-degrees).</summary>
    public const int RotationStep = 500;

    /// <summary>
    /// Centre of the key's unrotated rectangle, rotated about its pivot when
    /// <c>R ≠ 0</c>. Centi-u in, centi-u out.
    /// </summary>
    public static (double X, double Y) RotatedCenter(UserLayoutKey key) =>
        RotatedCenter(key.X, key.Y, key.W, key.H, key.R, key.Rx, key.Ry);

    /// <summary>Same rule as <see cref="RotatedCenter(UserLayoutKey)"/>, on a
    /// dtsi key (<c>Width/Height/X/Y/Rotation/Rx/Ry</c>).</summary>
    public static (double X, double Y) RotatedCenter(DtsiKeyPhysical key) =>
        RotatedCenter(key.X, key.Y, key.Width, key.Height, key.Rotation, key.Rx, key.Ry);

    /// <summary>The key's four corners after rotation, in TL, TR, BR, BL order.</summary>
    public static (double X, double Y)[] Corners(UserLayoutKey key) =>
    [
        RotatePoint(key.X, key.Y, key.Rx, key.Ry, key.R),
        RotatePoint(key.X + key.W, key.Y, key.Rx, key.Ry, key.R),
        RotatePoint(key.X + key.W, key.Y + key.H, key.Rx, key.Ry, key.R),
        RotatePoint(key.X, key.Y + key.H, key.Rx, key.Ry, key.R),
    ];

    /// <summary>
    /// Axis-aligned box containing every key's rotated corners. The empty
    /// sequence yields <c>(0, 0, 0, 0)</c>.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) RotatedBounds(
        IEnumerable<UserLayoutKey> keys)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var any = false;
        foreach (var key in keys)
        {
            any = true;
            foreach (var (cx, cy) in Corners(key))
            {
                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;
            }
        }
        return any ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    /// <summary>
    /// Whether (x, y) lies on or inside the rotated key: the point is rotated
    /// by −R about the pivot and tested against the unrotated rectangle.
    /// </summary>
    public static bool Contains(UserLayoutKey key, double x, double y)
    {
        if (key.R != 0)
            (x, y) = RotatePoint(x, y, key.Rx, key.Ry, -key.R);
        return x >= key.X && x <= key.X + key.W && y >= key.Y && y <= key.Y + key.H;
    }

    /// <summary>Index of the topmost (highest) key containing (x, y), or −1
    /// when no key does — later keys draw over earlier ones.</summary>
    public static int HitTest(IReadOnlyList<UserLayoutKey> keys, double x, double y)
    {
        for (var i = keys.Count - 1; i >= 0; i--)
        {
            if (Contains(keys[i], x, y))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Indices of the keys whose rotated centre lies inside the box (inclusive;
    /// the two corners may be given in any order), ascending.
    /// </summary>
    public static IReadOnlyList<int> InBox(
        IReadOnlyList<UserLayoutKey> keys, double x1, double y1, double x2, double y2)
    {
        var loX = Math.Min(x1, x2);
        var hiX = Math.Max(x1, x2);
        var loY = Math.Min(y1, y2);
        var hiY = Math.Max(y1, y2);

        var hits = new List<int>();
        for (var i = 0; i < keys.Count; i++)
        {
            var (cx, cy) = RotatedCenter(keys[i]);
            if (cx >= loX && cx <= hiX && cy >= loY && cy <= hiY)
                hits.Add(i);
        }
        return hits;
    }

    /// <summary>Nearest multiple of <paramref name="step"/>, halves away from
    /// zero — see <see cref="PositionStep"/> and <see cref="RotationStep"/>.</summary>
    public static int Snap(double value, int step) =>
        (int)Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>Wrap a centi-degree angle into (−18000, 18000].</summary>
    public static int NormalizeRotation(int centiDegrees)
    {
        var angle = centiDegrees % 36000;
        if (angle > 18000)
            angle -= 36000;
        else if (angle <= -18000)
            angle += 36000;
        return angle;
    }

    /// <summary>
    /// Shift the selected keys by (dx, dy): their X/Y always move, their
    /// Rx/Ry only when the key is rotated (an unrotated key's pivot is
    /// meaningless). Unselected keys are returned unchanged; a new list always.
    /// </summary>
    public static IReadOnlyList<UserLayoutKey> Move(
        IReadOnlyList<UserLayoutKey> keys, IReadOnlyCollection<int> indices, int dx, int dy)
    {
        var selected = new HashSet<int>(indices);
        var result = new List<UserLayoutKey>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (!selected.Contains(i))
            {
                result.Add(key);
                continue;
            }
            result.Add(key with
            {
                X = key.X + dx,
                Y = key.Y + dy,
                Rx = key.R != 0 ? key.Rx + dx : key.Rx,
                Ry = key.R != 0 ? key.Ry + dy : key.Ry,
            });
        }
        return result;
    }

    /// <summary>
    /// Rotate the selected keys' centres by <paramref name="deltaCentiDegrees"/>
    /// about (pivotX, pivotY). Each selected key ends up with its centre at the
    /// rotated position, angle <c>R + Δ</c> normalised, and — when it is then
    /// rotated — a pivot at its own centre; the old shared Glove80-style pivot
    /// is rewritten and looks identical. Unrotated results carry no pivot.
    /// W, H and Hand are unchanged; unselected keys are returned unchanged.
    /// </summary>
    public static IReadOnlyList<UserLayoutKey> Rotate(
        IReadOnlyList<UserLayoutKey> keys, IReadOnlyCollection<int> indices,
        int deltaCentiDegrees, double pivotX, double pivotY)
    {
        var selected = new HashSet<int>(indices);
        var result = new List<UserLayoutKey>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (!selected.Contains(i))
            {
                result.Add(key);
                continue;
            }

            var (cx, cy) = RotatedCenter(key);
            var centre = RotatePoint(cx, cy, pivotX, pivotY, deltaCentiDegrees);
            var rotation = NormalizeRotation(key.R + deltaCentiDegrees);
            result.Add(key with
            {
                X = RoundAway(centre.X - key.W / 2.0),
                Y = RoundAway(centre.Y - key.H / 2.0),
                R = rotation,
                Rx = rotation != 0 ? RoundAway(centre.X) : 0,
                Ry = rotation != 0 ? RoundAway(centre.Y) : 0,
            });
        }
        return result;
    }

    /// <summary>Re-hand every key by a vertical split line: the rotated centre
    /// strictly left of <paramref name="splitX"/> is Left, on or right of it is
    /// Right.</summary>
    public static IReadOnlyList<UserLayoutKey> ApplyHands(
        IReadOnlyList<UserLayoutKey> keys, int splitX)
    {
        var result = new List<UserLayoutKey>(keys.Count);
        foreach (var key in keys)
        {
            result.Add(key with
            {
                Hand = RotatedCenter(key).X < splitX ? Hand.Left : Hand.Right,
            });
        }
        return result;
    }

    /// <summary>
    /// Where the editor first drops the split line for a drafted layout: the
    /// rounded midpoint of the gap between the rightmost left-hand centre and
    /// the leftmost right-hand centre when the hands already sit on their own
    /// sides; otherwise
    /// <see cref="UserLayoutDraft.FindMidlineCentiU"/> over the keys.
    /// </summary>
    public static int InitialSplitX(IReadOnlyList<UserLayoutKey> keys)
    {
        double? leftMax = null;
        double? rightMin = null;
        foreach (var key in keys)
        {
            var cx = RotatedCenter(key).X;
            if (key.Hand == Hand.Left)
                leftMax = leftMax is null || cx > leftMax.Value ? cx : leftMax;
            else
                rightMin = rightMin is null || cx < rightMin.Value ? cx : rightMin;
        }

        if (leftMax is not null && rightMin is not null && leftMax < rightMin)
            return RoundAway((leftMax.Value + rightMin.Value) / 2.0);

        var dtsi = new List<DtsiKeyPhysical>(keys.Count);
        foreach (var key in keys)
        {
            dtsi.Add(new DtsiKeyPhysical(key.W, key.H, key.X, key.Y, key.R, key.Rx, key.Ry));
        }
        return UserLayoutDraft.FindMidlineCentiU(dtsi);
    }

    /// <summary>
    /// Shift every key so the rotated bounding box starts at (0, 0): the floor
    /// of the min rotated X/Y is brought up to 0, so afterwards both mins lie
    /// in [0, 1) and no tilted key sticks out at negative coordinates. Pivots
    /// of rotated keys move with their keys.
    /// </summary>
    public static IReadOnlyList<UserLayoutKey> Normalize(IReadOnlyList<UserLayoutKey> keys)
    {
        var (minX, minY, _, _) = RotatedBounds(keys);
        var dx = -(int)Math.Floor(minX);
        var dy = -(int)Math.Floor(minY);
        return Move(keys, Enumerable.Range(0, keys.Count).ToList(), dx, dy);
    }

    private static (double X, double Y) RotatedCenter(
        int x, int y, int w, int h, int rotation, int rx, int ry)
    {
        var cx = x + w / 2.0;
        var cy = y + h / 2.0;
        if (rotation == 0)
            return (cx, cy);

        var radians = rotation / 100.0 * Math.PI / 180.0;
        var dx = cx - rx;
        var dy = cy - ry;
        return (rx + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            ry + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    private static (double X, double Y) RotatePoint(
        double x, double y, double pivotX, double pivotY, int centiDegrees)
    {
        if (centiDegrees == 0)
            return (x, y);

        var radians = centiDegrees / 100.0 * Math.PI / 180.0;
        var dx = x - pivotX;
        var dy = y - pivotY;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return (pivotX + dx * cos - dy * sin, pivotY + dx * sin + dy * cos);
    }

    private static int RoundAway(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
