namespace ConsDTR.Runtime.Tasks;

/// <summary>Seeded non-language experimental inputs. Planted witnesses are not embedded in the task input.</summary>
public static class TaskProblemGenerator
{
    public static HornProblem Horn(int depth, int distractorAtoms, int seed, bool derivable = true, HornShape shape = HornShape.Chain)
    {
        if (depth < 1 || distractorAtoms < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var random = new Random(seed);
        var coreCount = shape == HornShape.ForkMerge ? checked(1 + 3 * depth) : checked(depth + 1);
        var names = PermutedLabels(checked(coreCount + distractorAtoms + 1), "a", random);
        var atoms = names.ToArray();
        var rules = new List<HornRule>();
        var facts = new List<string> { names[0] };
        var unavailable = names[^1];
        var current = names[0];
        for (var layer = 0; layer < depth; layer++)
        {
            var blocked = !derivable && layer == depth / 2;
            if (shape == HornShape.ForkMerge)
            {
                var left = names[1 + 3 * layer]; var right = names[2 + 3 * layer]; var next = names[3 + 3 * layer];
                rules.Add(new("r" + rules.Count, blocked ? [current, unavailable] : [current], left));
                rules.Add(new("r" + rules.Count, [current], right));
                rules.Add(new("r" + rules.Count, [left, right, left], next));
                current = next;
            }
            else
            {
                var next = names[layer + 1];
                rules.Add(new("r" + rules.Count, blocked ? [current, unavailable] : [current], next));
                current = next;
            }
        }
        if (shape == HornShape.Cyclic)
        {
            rules.Add(new("r" + rules.Count, [current], names[0]));
            if (depth > 1) rules.Add(new("r" + rules.Count, [names[1], names[1]], names[1]));
        }
        var noise = names.Skip(coreCount).Take(distractorAtoms).ToArray();
        foreach (var atom in noise)
            if (random.Next(2) == 0) facts.Add(atom);
        for (var index = 0; index < distractorAtoms * 2; index++)
        {
            var premise = noise[random.Next(noise.Length)];
            var conclusion = noise[random.Next(noise.Length)];
            rules.Add(new("n" + index, [premise], conclusion));
        }
        return new HornProblem(atoms, facts, rules, current);
    }

    public static GraphPathProblem Reachability(int vertexCount, int seed, bool reachable = true, double extraEdgeProbability = 0.15,
        bool requireSimplePath = false)
    {
        if (vertexCount < 2) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (!double.IsFinite(extraEdgeProbability) || extraEdgeProbability is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(extraEdgeProbability));
        var random = new Random(seed);
        var vertices = PermutedLabels(vertexCount, "v", random);
        var edges = new List<DirectedEdge>();
        var pairs = new HashSet<(int Source, int Target)>();
        var split = vertexCount / 2;
        void Add(int source, int target)
        {
            if (pairs.Add((source, target))) edges.Add(new DirectedEdge("e" + edges.Count, vertices[source], vertices[target]));
        }
        for (var index = 0; index < vertexCount - 1; index++)
            if (reachable || index != split - 1) Add(index, index + 1);
        for (var source = 0; source < vertexCount; source++)
        for (var target = 0; target < vertexCount; target++)
            if (source != target && (reachable || !(source < split && target >= split)) && random.NextDouble() < extraEdgeProbability)
                Add(source, target);
        return new GraphPathProblem(vertices, edges, vertices[0], vertices[^1], requireSimplePath);
    }

    public static CspProblem BooleanCsp(int variableCount, int constraintCount, int seed, bool satisfiable = true)
    {
        if (variableCount < 1 || constraintCount < 0) throw new ArgumentOutOfRangeException(nameof(variableCount));
        var random = new Random(seed);
        var labels = PermutedLabels(variableCount, "x", random);
        var variables = labels.Select(label => new CspVariable(label, [0, 1])).ToArray();
        var witness = labels.ToDictionary(label => label, _ => random.Next(2), StringComparer.Ordinal);
        var constraints = new List<FiniteConstraint>();
        for (var index = 0; index < constraintCount; index++)
        {
            var literals = Enumerable.Range(0, Math.Min(variableCount, 3)).Select(_ =>
            {
                var variable = labels[random.Next(labels.Length)];
                return new FiniteConstraint.BooleanLiteral(variable, random.Next(2) == 0);
            }).ToArray();
            // Plant one true literal, leaving the other literal choices and hidden satisfying assignment random.
            literals[0] = new FiniteConstraint.BooleanLiteral(literals[0].Variable, witness[literals[0].Variable] == 0);
            constraints.Add(FiniteConstraint.Clause("c" + index, literals));
        }
        if (!satisfiable)
        {
            constraints.Add(FiniteConstraint.Clause("pressure:positive", [new(labels[0])]));
            constraints.Add(FiniteConstraint.Clause("pressure:negative", [new(labels[0], true)]));
        }
        return new CspProblem(variables, constraints);
    }

    private static string[] PermutedLabels(int count, string prefix, Random random)
    {
        var labels = Enumerable.Range(0, count).Select(index => prefix + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        random.Shuffle(labels);
        return labels;
    }

    public enum HornShape { Chain, ForkMerge, Cyclic }
}
