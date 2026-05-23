namespace LViz.Core.Keymap.Parser;

/// <summary>Root of a parsed devicetree overlay.</summary>
public sealed record DtRoot(IReadOnlyList<DtNode> Children);

/// <summary>
/// A devicetree node. Can be a fresh declaration (<c>name { … };</c> or
/// <c>label: name { … };</c>) or an overlay against an existing phandle
/// (<c>&amp;phandle { … };</c>, in which case <see cref="IsReference"/>
/// is true and <see cref="Name"/> carries the phandle name without '&amp;').
/// </summary>
public sealed record DtNode(
    string? Label,
    string Name,
    bool IsReference,
    IReadOnlyList<DtProperty> Properties,
    IReadOnlyList<DtNode> Children,
    int Line,
    int Column);

public sealed record DtProperty(string Name, DtValue Value, int Line, int Column);

public abstract record DtValue;

/// <summary>Property declared with no '=' value — boolean-style flag.</summary>
public sealed record DtBool : DtValue;

public sealed record DtStringList(IReadOnlyList<string> Values) : DtValue;

public sealed record DtCellArray(IReadOnlyList<DtCell> Cells) : DtValue;

public sealed record DtByteArray(IReadOnlyList<byte> Bytes) : DtValue;

public abstract record DtCell;

public sealed record DtCellNumber(int Value, string RawText) : DtCell;

/// <summary>A reference to another node by phandle — appears as
/// <c>&amp;name</c> inside <c>&lt;…&gt;</c>. <see cref="PhandleName"/>
/// excludes the leading '&amp;'.</summary>
public sealed record DtCellRef(string PhandleName) : DtCell;

/// <summary>A bare identifier inside <c>&lt;…&gt;</c> — typically a
/// keycode token (<c>A</c>, <c>LSHFT</c>, <c>LPAR</c>) that survived
/// preprocessing without being expanded to a number.</summary>
public sealed record DtCellIdent(string Text) : DtCell;

/// <summary>A parenthesized sub-expression inside <c>&lt;…&gt;</c>, kept
/// verbatim. Most often a modifier-wrapped keycode like
/// <c>(LC(LSFT))</c> that downstream consumers (e.g.
/// <see cref="LViz.Core.Keymap.ZmkKeycodeMapper"/>) need to see whole.</summary>
public sealed record DtCellParenExpr(string RawText) : DtCell;
