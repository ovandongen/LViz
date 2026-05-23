using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Keymap;

public class PreprocessorTests
{
    [Fact]
    public void StripsLineComments()
    {
        var result = Preprocessor.Process("foo // a comment\nbar");
        Assert.Contains("foo", result);
        Assert.DoesNotContain("comment", result);
        Assert.Contains("bar", result);
    }

    [Fact]
    public void StripsBlockComments()
    {
        var result = Preprocessor.Process("foo /* hidden */ bar");
        Assert.Contains("foo", result);
        Assert.Contains("bar", result);
        Assert.DoesNotContain("hidden", result);
    }

    [Fact]
    public void PreservesNewlinesInsideBlockComments()
    {
        // Line numbers in downstream errors must still line up with source.
        var result = Preprocessor.Process("a\n/* line2\nline3\n*/ b");
        var lines = result.Split('\n');
        // 'b' should be on line 4 (0-indexed: line 3).
        Assert.Contains("b", lines[3]);
    }

    [Fact]
    public void LeavesCommentLikeSequencesInStringsAlone()
    {
        var result = Preprocessor.Process("display-name = \"// not a comment\";");
        Assert.Contains("// not a comment", result);
    }

    [Fact]
    public void ObjectLikeDefineSubstitutes()
    {
        var result = Preprocessor.Process("#define LOWER 1\n&mo LOWER");
        Assert.Contains("&mo 1", result);
    }

    [Fact]
    public void DefineSubstitutionIsWordBounded()
    {
        // LOWER should not match LOWERCASE.
        var result = Preprocessor.Process("#define LOWER 1\nLOWERCASE");
        Assert.Contains("LOWERCASE", result);
        Assert.DoesNotContain("1CASE", result);
    }

    [Fact]
    public void UndefRemovesDefine()
    {
        var result = Preprocessor.Process("#define X 1\n#undef X\nX");
        Assert.DoesNotContain(" 1", result);
        Assert.Contains("X", result);
    }

    [Fact]
    public void FunctionLikeDefineIsSkippedSilently()
    {
        // We don't crash, and we don't substitute. The macro just disappears.
        var result = Preprocessor.Process("#define HM(M,K) &mt M K\nHM(LSHFT, A)");
        Assert.Contains("HM(LSHFT, A)", result);
    }

    [Fact]
    public void IfdefBlockIsStrippedWhenUndefined()
    {
        var src = "#ifdef CONFIG_FOO\nhidden\n#endif\nvisible";
        var result = Preprocessor.Process(src);
        Assert.DoesNotContain("hidden", result);
        Assert.Contains("visible", result);
    }

    [Fact]
    public void IfdefBlockIsKeptWhenDefined()
    {
        var src = "#define FOO 1\n#ifdef FOO\nkept\n#endif";
        var result = Preprocessor.Process(src);
        Assert.Contains("kept", result);
    }

    [Fact]
    public void IfndefSelectsOppositeBranch()
    {
        var src = "#ifndef UNDEFINED\nyes\n#else\nno\n#endif";
        var result = Preprocessor.Process(src);
        Assert.Contains("yes", result);
        Assert.DoesNotContain("no", result);
    }

    [Fact]
    public void IfWithDefinedOperator()
    {
        var src = "#define A 1\n#if defined(A) && !defined(B)\nyes\n#endif";
        var result = Preprocessor.Process(src);
        Assert.Contains("yes", result);
    }

    [Fact]
    public void IncludeLinesAreDroppedSilently()
    {
        // No exception, no leftover '#include' text.
        var result = Preprocessor.Process("#include <dt-bindings/zmk/keys.h>\nbody");
        Assert.DoesNotContain("#include", result);
        Assert.Contains("body", result);
    }

    [Fact]
    public void UnknownIdentifierInIfEvaluatesAsZero()
    {
        var src = "#if CONFIG_NOT_SET\nhidden\n#endif\nvisible";
        var result = Preprocessor.Process(src);
        Assert.DoesNotContain("hidden", result);
        Assert.Contains("visible", result);
    }

    [Fact]
    public void NestedIfdefsRestoreParentActiveState()
    {
        // Inner #endif must not unconditionally re-enable an outer disabled
        // block — the parent's active state should be restored.
        var src = "#ifdef OUTER\n  outer\n  #ifdef INNER\n    inner\n  #endif\n  after_inner\n#endif\nbottom";
        var result = Preprocessor.Process(src);
        Assert.DoesNotContain("outer", result);
        Assert.DoesNotContain("inner", result);
        Assert.DoesNotContain("after_inner", result);
        Assert.Contains("bottom", result);
    }
}
