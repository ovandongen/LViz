using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="PipelineGraph.FindCycle"/>: the edit-time cycle detector over
/// the pipeline reference graph (Pipeline-step refs). Returns the offending chain or
/// null; ignores dangling refs and non-Pipeline steps.
/// </summary>
public class PipelineGraphTests
{
    private static ActionPipeline Pipe(string name, params string[] refs) =>
        new(name, refs.Select(r => new PipelineStep(PipelineStepKind.Pipeline, PipelineRef: r)).ToList());

    [Fact]
    public void NoRefs_ReturnsNull()
    {
        var a = new ActionPipeline("a", new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) });
        Assert.Null(PipelineGraph.FindCycle(new[] { a }, "a"));
    }

    [Fact]
    public void Acyclic_Chain_ReturnsNull()
    {
        var lib = new[] { Pipe("a", "b"), Pipe("b", "c"), Pipe("c") };
        Assert.Null(PipelineGraph.FindCycle(lib, "a"));
    }

    [Fact]
    public void DirectSelfReference_ReturnsCycle()
    {
        var cycle = PipelineGraph.FindCycle(new[] { Pipe("a", "a") }, "a");
        Assert.Equal(new[] { "a", "a" }, cycle);
    }

    [Fact]
    public void IndirectCycle_ReturnsChain()
    {
        var lib = new[] { Pipe("a", "b"), Pipe("b", "c"), Pipe("c", "a") };
        var cycle = PipelineGraph.FindCycle(lib, "a");
        Assert.Equal(new[] { "a", "b", "c", "a" }, cycle);
    }

    [Fact]
    public void DanglingRef_IsIgnored()
    {
        var lib = new[] { Pipe("a", "missing") };
        Assert.Null(PipelineGraph.FindCycle(lib, "a"));
    }

    [Fact]
    public void DiamondReuse_IsNotACycle()
    {
        var lib = new[] { Pipe("a", "b", "c"), Pipe("b", "d"), Pipe("c", "d"), Pipe("d") };
        Assert.Null(PipelineGraph.FindCycle(lib, "a"));
    }

    [Fact]
    public void CycleNotReachableFromStart_ReturnsNull()
    {
        // b ↔ c loop exists, but nothing reachable from "a" enters it.
        var lib = new[] { Pipe("a"), Pipe("b", "c"), Pipe("c", "b") };
        Assert.Null(PipelineGraph.FindCycle(lib, "a"));
    }
}
