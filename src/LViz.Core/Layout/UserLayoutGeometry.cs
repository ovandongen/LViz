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

    /// <summary>
    /// The deltas one move-drag should apply, per axis in centi-u, plus the
    /// alignment line each axis locked onto (centi-u on that same axis) —
    /// null when that axis fell back to the <see cref="PositionStep"/> grid.
    /// </summary>
    public sealed record SnapResult(int Dx, int Dy, double? GuideX, double? GuideY);

    /// <summary>
    /// Snap a move-drag of the <paramref name="moving"/> selection to the keys
    /// that stay put: edges and centres, flush counts as aligned, MoErgo pitch —
    /// neighbouring keys touch in units, so "next to" means edge on edge, not
    /// gap to gap. The axes are decided independently; see
    /// <see cref="SnapAxis"/> for the candidate order. A hit returns the shift
    /// that closes the gap and reports the edge it closed to as a guide; a miss
    /// falls back to <see cref="Snap(double, int)"/> on <see cref="PositionStep"/>
    /// with no guide. Rotated keys take part through their rotated bounding box;
    /// rotation itself never snaps to neighbours. Last, a move that would leave
    /// a key on top of another is pushed flush against it (see
    /// <see cref="PushOutOfOverlap"/>). Pure — nothing moves, the caller applies
    /// the delta with <see cref="Move"/>.
    /// </summary>
    public static SnapResult SnapMove(
        IReadOnlyList<UserLayoutKey> keys, IReadOnlyCollection<int> moving,
        double rawDx, double rawDy, double tolerance)
    {
        var selected = new HashSet<int>(moving);
        if (selected.Count == 0)
            return GridResult(rawDx, rawDy);

        var moved = new List<UserLayoutKey>(selected.Count);
        var still = new List<UserLayoutKey>(keys.Count - selected.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            (selected.Contains(i) ? moved : still).Add(keys[i]);
        }
        // Nothing moving (indices outside the list) or nothing to align to —
        // the plain grid, and no guide on either axis.
        if (moved.Count == 0 || still.Count == 0)
            return GridResult(rawDx, rawDy);

        var (dx, guideX) = SnapAxis(moved, still, rawDx, tolerance, horizontal: true);
        var (dy, guideY) = SnapAxis(moved, still, rawDy, tolerance, horizontal: false);
        return PushOutOfOverlap(moved, still, new SnapResult(dx, dy, guideX, guideY));
    }

    /// <summary>
    /// When the snapped move leaves a moving key overlapping a still one, push
    /// the selection flush against that key: every overlapping pair offers its
    /// four flush shifts (ours against their left, right, top, bottom), and the
    /// smallest one that leaves no overlap at all wins — so a key dropped
    /// between two neighbours lands in the gap, not on the next key. That axis
    /// takes the flush edge as its guide; the other axis keeps its own snap.
    /// When no flush shift is free, the move stays as it was. Only unrotated
    /// keys take part: a rotated key's bounding box overlaps its neighbours'
    /// even when the keys themselves don't (an angled thumb cluster), so
    /// pushing on it would fight the arranging.
    /// </summary>
    private static SnapResult PushOutOfOverlap(
        IReadOnlyList<UserLayoutKey> moved, IReadOnlyList<UserLayoutKey> still, SnapResult snap)
    {
        if (moved.Any(key => key.R != 0))
            return snap;
        var flat = still.Where(key => key.R == 0).ToList();

        static bool Overlaps(UserLayoutKey m, int dx, int dy, UserLayoutKey b) =>
            m.X + dx < b.X + b.W && b.X < m.X + dx + m.W
            && m.Y + dy < b.Y + b.H && b.Y < m.Y + dy + m.H;

        bool Free(int dx, int dy) =>
            !moved.Any(m => flat.Any(b => Overlaps(m, dx, dy, b)));

        if (Free(snap.Dx, snap.Dy))
            return snap;

        SnapResult? best = null;
        var bestShift = 0;
        void Consider(int dx, int dy, double? guideX, double? guideY)
        {
            var shift = Math.Abs(dx - snap.Dx) + Math.Abs(dy - snap.Dy);
            if (best is not null && shift >= bestShift)
                return;
            if (!Free(dx, dy))
                return;
            best = new SnapResult(dx, dy, guideX, guideY);
            bestShift = shift;
        }

        foreach (var m in moved)
        {
            foreach (var b in flat)
            {
                if (!Overlaps(m, snap.Dx, snap.Dy, b))
                    continue;
                Consider(b.X - m.W - m.X, snap.Dy, b.X, snap.GuideY);
                Consider(b.X + b.W - m.X, snap.Dy, b.X + b.W, snap.GuideY);
                Consider(snap.Dx, b.Y - m.H - m.Y, snap.GuideX, b.Y);
                Consider(snap.Dx, b.Y + b.H - m.Y, snap.GuideX, b.Y + b.H);
            }
        }
        return best ?? snap;
    }

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

    /// <summary>How far apart, centi-u, the innermost key centres of the two
    /// halves have to be for <see cref="HasSplitGap"/> to call a layout split.</summary>
    public const int MinSplitGap = 125;

    /// <summary>
    /// Whether the layout looks like a split board: the rightmost left-hand key
    /// centre and the leftmost right-hand one are at least
    /// <see cref="MinSplitGap"/> apart. On a unibody some row has keys touching
    /// across the middle, 1u or less centre to centre. Centres rather than
    /// bounds, because angled inner thumbs (Corne, Glove80, Kyria) nearly touch
    /// even on a split board — the bundled boards range from 1.52u (Corne) up.
    /// Layouts carry no split flag, so this is the guess for an imported or
    /// saved one.
    /// </summary>
    public static bool HasSplitGap(IReadOnlyList<UserLayoutKey> keys)
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

        return leftMax is not null && rightMin is not null
            && rightMin.Value - leftMax.Value >= MinSplitGap;
    }

    /// <summary>
    /// <see cref="Mirror(IReadOnlyList{UserLayoutKey}, Hand, double)"/> about
    /// the middle between the halves: halfway between the average key centre
    /// of each half, so the target half as a whole stays where it was — only
    /// its keys are rearranged — wherever the split line happens to sit.
    /// Null under the same conditions.
    /// </summary>
    public static IReadOnlyList<UserLayoutKey>? Mirror(IReadOnlyList<UserLayoutKey> keys, Hand from)
    {
        double sourceSum = 0, targetSum = 0;
        int sourceCount = 0, targetCount = 0;
        foreach (var key in keys)
        {
            var (cx, _) = RotatedCenter(key);
            if (key.Hand == from)
            {
                sourceSum += cx;
                sourceCount++;
            }
            else
            {
                targetSum += cx;
                targetCount++;
            }
        }
        if (sourceCount == 0 || sourceCount != targetCount)
            return null;

        return Mirror(keys, from, (sourceSum / sourceCount + targetSum / targetCount) / 2);
    }

    /// <summary>
    /// Flip the <paramref name="from"/> half about the vertical line at
    /// <paramref name="splitX"/> and drop the other half's keys onto those
    /// mirrored positions. Each target keeps its index (its binding order) and
    /// takes the nearest still-free mirrored position — pairs are matched
    /// greedily by distance — so a roughly placed grid maps column to column
    /// instead of shuffling keys across the half. A moved key takes the source's
    /// W/H and the negated rotation, with its pivot at its own centre when it
    /// ends up rotated; the source keys and their hands are untouched. Centres
    /// mirror as <c>x → 2·splitX − x</c>, y unchanged. Returns a new list in the
    /// original index order, or null when the halves can't pair one-to-one
    /// (an empty source half, or different key counts).
    /// </summary>
    public static IReadOnlyList<UserLayoutKey>? Mirror(
        IReadOnlyList<UserLayoutKey> keys, Hand from, double splitX)
    {
        var sources = new List<int>();
        var targets = new List<int>();
        for (var i = 0; i < keys.Count; i++)
        {
            (keys[i].Hand == from ? sources : targets).Add(i);
        }
        if (sources.Count == 0 || sources.Count != targets.Count)
            return null;

        // One mirrored slot per source key, in source order.
        var slots = new List<(double X, double Y, int R, int W, int H)>(sources.Count);
        foreach (var s in sources)
        {
            var source = keys[s];
            var (cx, cy) = RotatedCenter(source);
            slots.Add((2.0 * splitX - cx, cy,
                NormalizeRotation(-source.R), source.W, source.H));
        }

        // Every (target, slot) distance, closest first; ties break on the target
        // index then the slot, so the pairing is deterministic.
        var pairs = new List<(double Distance, int Target, int Slot)>(targets.Count * slots.Count);
        foreach (var t in targets)
        {
            var (tx, ty) = RotatedCenter(keys[t]);
            for (var p = 0; p < slots.Count; p++)
            {
                var dx = tx - slots[p].X;
                var dy = ty - slots[p].Y;
                pairs.Add((Math.Sqrt(dx * dx + dy * dy), t, p));
            }
        }
        pairs.Sort(static (a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
                return byDistance;
            return a.Target != b.Target ? a.Target.CompareTo(b.Target) : a.Slot.CompareTo(b.Slot);
        });

        // Greedy: closest pair first, each target and each slot used once. Equal
        // half sizes mean every target ends up paired by the end of the walk.
        var takenSlots = new HashSet<int>();
        var assigned = new Dictionary<int, (double X, double Y, int R, int W, int H)>();
        foreach (var pair in pairs)
        {
            if (assigned.ContainsKey(pair.Target) || !takenSlots.Add(pair.Slot))
                continue;
            assigned[pair.Target] = slots[pair.Slot];
        }

        var result = new List<UserLayoutKey>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (!assigned.TryGetValue(i, out var mirrored))
            {
                result.Add(key);
                continue;
            }
            result.Add(key with
            {
                X = RoundAway(mirrored.X - mirrored.W / 2.0),
                Y = RoundAway(mirrored.Y - mirrored.H / 2.0),
                W = mirrored.W,
                H = mirrored.H,
                R = mirrored.R,
                Rx = mirrored.R != 0 ? RoundAway(mirrored.X) : 0,
                Ry = mirrored.R != 0 ? RoundAway(mirrored.Y) : 0,
            });
        }
        return result;
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

    /// <summary>
    /// One axis of <see cref="SnapMove"/>. The selection's rotated bounds shifted
    /// by <paramref name="raw"/> are compared with every still key's rotated
    /// bounds, keys in index order, and per key these candidate pairs
    /// (source → target) in order: min → min (left/top on left/top),
    /// centre → centre, max → max, min → max (flush: ours butts up against theirs),
    /// max → min (flush the other way). The winner is the smallest |target − source|
    /// that is still within <paramref name="tolerance"/>; ties keep the first
    /// candidate found. No winner → the grid, no guide.
    /// </summary>
    private static (int Delta, double? Guide) SnapAxis(
        IReadOnlyList<UserLayoutKey> moved, IReadOnlyList<UserLayoutKey> still,
        double raw, double tolerance, bool horizontal)
    {
        var (minX, minY, maxX, maxY) = RotatedBounds(moved);
        var min = (horizontal ? minX : minY) + raw;
        var max = (horizontal ? maxX : maxY) + raw;
        var centre = (min + max) / 2.0;

        double? best = null;
        var guide = 0.0;
        void Consider(double source, double target)
        {
            var offset = target - source;
            if (Math.Abs(offset) > tolerance)
                return;
            if (best is null || Math.Abs(offset) < Math.Abs(best.Value))
            {
                best = offset;
                guide = target;
            }
        }

        foreach (var key in still)
        {
            var (bMinX, bMinY, bMaxX, bMaxY) = RotatedBounds([key]);
            var bMin = horizontal ? bMinX : bMinY;
            var bMax = horizontal ? bMaxX : bMaxY;

            Consider(min, bMin);
            Consider(centre, (bMin + bMax) / 2.0);
            Consider(max, bMax);
            Consider(min, bMax);
            Consider(max, bMin);
        }

        return best is null
            ? (Snap(raw, PositionStep), null)
            : (RoundAway(raw + best.Value), guide);
    }

    private static SnapResult GridResult(double rawDx, double rawDy) =>
        new(Snap(rawDx, PositionStep), Snap(rawDy, PositionStep), null, null);

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
