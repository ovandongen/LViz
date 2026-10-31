using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// The wizard's "Build from questions" answers: split or unibody, main keys
/// per half without thumb keys (all main keys when unibody), columns per
/// half (whole board when unibody) and thumb keys per half. The row count
/// follows from the key count: full rows of <see cref="GridLayoutOptions.Columns"/>
/// keys, then one shorter last row. There is no stagger question — stagger
/// and angles are added afterwards in the editor.
/// </summary>
public sealed record GridLayoutOptions(bool Split, int KeysPerHalf, int Columns, int ThumbKeys);

/// <summary>
/// Builds a plain grid <see cref="ImportedLayout"/> straight from
/// <see cref="GridLayoutOptions"/> without reading any board file — the
/// generator behind the wizard's "Build from questions" option. The
/// <c>N</c> main keys per half fill full rows of <c>C</c> = Columns, so
/// there are ceil(N/C) rows and the last one is shorter: on a split board
/// it hugs the split (left keys end at the last left column, right keys
/// start at the inner column), on a unibody it is centred. Keys are 1u
/// with no gaps inside a half; a split board gaps 2u between the halves.
/// The thumb row sits directly below the bottom row: on a split board the
/// left thumbs end at the inner column and the right ones start at it, on
/// a unibody they are centred. Order is ZMK keymap binding order — row by
/// row, left half before right half within a row, thumb row last — because
/// key index = binding index. Pure function, no I/O.
/// </summary>
public static class GridLayoutGenerator
{
    public const int MinKeysPerHalf = 1, MaxKeysPerHalf = 96, MinColumns = 1, MaxColumns = 12, MinThumbKeys = 0, MaxThumbKeys = 8;

    private const int Unit = 100; // one key in centi-u
    private const int SplitGapColumns = 2; // 2u gap between the halves

    /// <summary>How many keys <see cref="Generate"/> will emit. Validates
    /// exactly like <see cref="Generate"/> and throws the same.</summary>
    public static int KeyCount(GridLayoutOptions options)
    {
        Validate(options);
        return options.Split
            ? 2 * (options.KeysPerHalf + options.ThumbKeys)
            : options.KeysPerHalf + options.ThumbKeys;
    }

    public static ImportedLayout Generate(GridLayoutOptions options)
    {
        Validate(options);
        int n = options.KeysPerHalf;
        int c = options.Columns;
        int t = options.ThumbKeys;
        int rows = (n + c - 1) / c;    // full rows of C, then one shorter row
        int last = n - (rows - 1) * c; // 1..C keys on that last row
        int thumbY = rows * Unit;

        var keys = new List<DtsiKeyPhysical>(KeyCount(options));
        var hands = new List<Hand>(keys.Capacity);
        void Add(int x, int y, Hand hand)
        {
            keys.Add(new DtsiKeyPhysical(Unit, Unit, x, y, 0, 0, 0));
            hands.Add(hand);
        }

        if (options.Split)
        {
            for (var r = 0; r < rows; r++)
            {
                // The short last row hugs the split: the left keys end at
                // the last left column, the right ones start at the inner
                // column. With last == C this is an ordinary full row.
                int count = r < rows - 1 ? c : last;
                int leftStart = r < rows - 1 ? 0 : c - last;
                for (var i = 0; i < count; i++)
                    Add((leftStart + i) * Unit, r * Unit, Hand.Left);
                for (var i = 0; i < count; i++)
                    Add((c + SplitGapColumns + i) * Unit, r * Unit, Hand.Right);
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
            for (var r = 0; r < rows; r++)
            {
                // The short last row is centred like the thumb row; with an
                // odd (C − last) that starts on a half step.
                int count = r < rows - 1 ? c : last;
                int x0 = r < rows - 1 ? 0 : (c - last) * Unit / 2;
                for (var i = 0; i < count; i++)
                {
                    int x = x0 + i * Unit;
                    Add(x, r * Unit, HandOf(x, midline));
                }
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
        ArgumentOutOfRangeException.ThrowIfLessThan(options.KeysPerHalf, MinKeysPerHalf, nameof(options.KeysPerHalf));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.KeysPerHalf, MaxKeysPerHalf, nameof(options.KeysPerHalf));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Columns, MinColumns, nameof(options.Columns));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Columns, MaxColumns, nameof(options.Columns));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThumbKeys, MinThumbKeys, nameof(options.ThumbKeys));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ThumbKeys, MaxThumbKeys, nameof(options.ThumbKeys));
    }
}
