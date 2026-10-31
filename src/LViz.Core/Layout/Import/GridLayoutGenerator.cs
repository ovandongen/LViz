using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// The wizard's "Build from questions" answers: split or unibody, main rows,
/// columns per half (whole board when unibody) and thumb keys per half.
/// There is no stagger question — stagger and angles are added afterwards
/// in the editor.
/// </summary>
public sealed record GridLayoutOptions(bool Split, int Rows, int Columns, int ThumbKeys);

/// <summary>
/// Builds a plain grid <see cref="ImportedLayout"/> straight from
/// <see cref="GridLayoutOptions"/> without reading any board file — the
/// generator behind the wizard's "Build from questions" option. Keys are 1u
/// with no gaps inside a half; a split board gaps 2u between the halves.
/// The thumb row sits directly below the bottom row: on a split board it
/// starts at the inner column and runs outward, on a unibody it is centred.
/// Order is ZMK keymap binding order — row by row, left half before right
/// half within a row, thumb row last — because key index = binding index.
/// Pure function, no I/O.
/// </summary>
public static class GridLayoutGenerator
{
    public const int MinRows = 1, MaxRows = 8, MinColumns = 1, MaxColumns = 12, MinThumbKeys = 0, MaxThumbKeys = 8;

    private const int Unit = 100; // one key in centi-u
    private const int SplitGapColumns = 2; // 2u gap between the halves

    /// <summary>How many keys <see cref="Generate"/> will emit. Validates
    /// exactly like <see cref="Generate"/> and throws the same.</summary>
    public static int KeyCount(GridLayoutOptions options)
    {
        Validate(options);
        int main = options.Rows * options.Columns;
        return options.Split ? 2 * (main + options.ThumbKeys) : main + options.ThumbKeys;
    }

    public static ImportedLayout Generate(GridLayoutOptions options)
    {
        Validate(options);
        int c = options.Columns;
        int t = options.ThumbKeys;
        int thumbY = options.Rows * Unit;

        var keys = new List<DtsiKeyPhysical>(KeyCount(options));
        var hands = new List<Hand>(keys.Capacity);
        void Add(int x, int y, Hand hand)
        {
            keys.Add(new DtsiKeyPhysical(Unit, Unit, x, y, 0, 0, 0));
            hands.Add(hand);
        }

        if (options.Split)
        {
            for (var r = 0; r < options.Rows; r++)
            {
                for (var col = 0; col < c; col++)
                    Add(col * Unit, r * Unit, Hand.Left);
                for (var col = 0; col < c; col++)
                    Add((c + SplitGapColumns + col) * Unit, r * Unit, Hand.Right);
            }
            if (t > 0)
            {
                // Left thumbs end at the inner column; with T > C the outer
                // ones get negative x, which is fine because saving normalises.
                for (var i = 0; i < t; i++)
                    Add((c - t + i) * Unit, thumbY, Hand.Left);
                for (var i = 0; i < t; i++)
                    Add((c + SplitGapColumns + i) * Unit, thumbY, Hand.Right);
            }
        }
        else
        {
            // Unibody hands: the board's own midline at C·50 centi-u.
            int midline = c * Unit / 2;
            for (var r = 0; r < options.Rows; r++)
            {
                for (var col = 0; col < c; col++)
                    Add(col * Unit, r * Unit, HandOf(col * Unit, midline));
            }
            if (t > 0)
            {
                for (var i = 0; i < t; i++)
                {
                    int x = (c - t) * Unit / 2 + i * Unit; // centred; may be a half step
                    Add(x, thumbY, HandOf(x, midline));
                }
            }
        }

        return new ImportedLayout("grid", LayoutSource.Generated, keys, [], hands);
    }

    private static Hand HandOf(int x, int midline) =>
        x + Unit / 2 < midline ? Hand.Left : Hand.Right; // key centre X vs midline

    private static void Validate(GridLayoutOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Rows, MinRows, nameof(options.Rows));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Rows, MaxRows, nameof(options.Rows));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Columns, MinColumns, nameof(options.Columns));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Columns, MaxColumns, nameof(options.Columns));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThumbKeys, MinThumbKeys, nameof(options.ThumbKeys));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ThumbKeys, MaxThumbKeys, nameof(options.ThumbKeys));
    }
}
