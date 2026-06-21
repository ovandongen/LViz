namespace LViz.Core.Settings;

/// <summary>
/// Reachability/cycle analysis over the action-pipeline reference graph — the edges
/// are <see cref="PipelineStepKind.Pipeline"/> steps (a pipeline → the pipeline its
/// <see cref="PipelineStep.PipelineRef"/> names). Used at edit time to reject a
/// pipeline that would call itself transitively; the runner has its own runtime
/// guard as the backstop, so this is purely for a clear up-front message.
/// </summary>
public static class PipelineGraph
{
    /// <summary>
    /// Returns the cycle chain reachable from <paramref name="startName"/> via
    /// <c>Pipeline</c> steps — e.g. <c>["A","B","A"]</c> — or null if none. Names are
    /// matched ordinally; dangling refs (no pipeline of that name) are ignored. When
    /// several pipelines share a name the last in <paramref name="library"/> wins,
    /// mirroring how the runner resolves a name.
    /// </summary>
    public static IReadOnlyList<string>? FindCycle(IReadOnlyList<ActionPipeline> library, string startName)
    {
        var byName = new Dictionary<string, ActionPipeline>(StringComparer.Ordinal);
        foreach (var p in library) byName[p.Name] = p;

        var stack = new List<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<string>? Visit(string name)
        {
            if (onStack.Contains(name))
            {
                // Re-entered a name already on the current path → cycle. Return the
                // slice from its first occurrence, closed back onto itself.
                var from = stack.IndexOf(name);
                var cycle = stack.GetRange(from, stack.Count - from);
                cycle.Add(name);
                return cycle;
            }
            if (!done.Add(name)) return null;             // fully explored, acyclic
            if (!byName.TryGetValue(name, out var pipeline)) return null; // dangling

            stack.Add(name);
            onStack.Add(name);
            foreach (var step in pipeline.Steps)
            {
                if (step.Kind != PipelineStepKind.Pipeline || string.IsNullOrWhiteSpace(step.PipelineRef))
                    continue;
                var cycle = Visit(step.PipelineRef!);
                if (cycle is not null) return cycle;
            }
            stack.RemoveAt(stack.Count - 1);
            onStack.Remove(name);
            return null;
        }

        return Visit(startName);
    }
}
