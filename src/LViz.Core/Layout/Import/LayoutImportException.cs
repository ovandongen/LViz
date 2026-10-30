namespace LViz.Core.Layout.Import;

/// <summary>
/// A board file was refused because it is malformed. The message names the
/// offending node or property and carries the 1-based line number — import
/// errors are loud, matching the <c>.keymap</c> parser's behaviour.
/// </summary>
public sealed class LayoutImportException(string message) : Exception(message);
