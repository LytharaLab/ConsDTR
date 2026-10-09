using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Models;

/// <summary>
/// Shared bounded recurrent processor. Only supplied hard data edges transport neural messages.
/// This module proposes actions; it has no access to the exact kernel or certificate acceptance.
/// </summary>
public sealed class DtrNeuralModel : nn.Module<Tensor, Tensor>
{
    private readonly nn.Module<Tensor, Tensor> _encoder;
    private readonly nn.Module<Tensor, Tensor> _initial;
    private readonly nn.Module<Tensor, Tensor> _update;
    private readonly nn.Module<Tensor, Tensor> _updateGate;
    private readonly nn.Module<Tensor, Tensor> _messageGate;
    private readonly nn.Module<Tensor, Tensor> _router;
    private readonly nn.Module<Tensor, Tensor> _context;
    private readonly nn.Module<Tensor, Tensor> _prefixUpdate;
    private readonly nn.Module<Tensor, Tensor> _intentHead;
    private readonly nn.Module<Tensor, Tensor> _editHead;
    private readonly nn.Module<Tensor, Tensor> _operationHead;
    private readonly nn.Module<Tensor, Tensor> _portHead;
    private readonly nn.Module<Tensor, Tensor> _relationHead;
    private readonly nn.Module<Tensor, Tensor> _stopHead;
    private readonly nn.Module<Tensor, Tensor> _bitHead;
    private readonly nn.Module<Tensor, Tensor> _typeHead;
    private readonly nn.Module<Tensor, Tensor> _lengthHead;
    private readonly nn.Module<Tensor, Tensor> _continueHead;
    private readonly nn.Module<Tensor, Tensor> _pointerHead;
    private readonly nn.Module<Tensor, Tensor> _valueHead;
    private readonly nn.Module<Tensor, Tensor> _readinessHead;
    private Parameter _relationV;
    private Parameter _relationU;
    private Parameter _relationD;
    private Parameter _relationBias;
    private Parameter _relationEmbedding;
    private Parameter _fieldEmbedding;
    private Parameter _choiceEmbedding;

    public DtrModelConfig Config { get; }
    public Device Device { get; }
    public bool IsFrozen { get; private set; }
    public long ParameterVersion { get; internal set; }
    public long ParameterCount => parameters().Sum(parameter => parameter.numel());

    public DtrNeuralModel(DtrModelConfig? config = null, string device = "cpu") : base("ConsDTRv1")
    {
        Config = config ?? new DtrModelConfig();
        Config.Validate();
        Device = DtrDevice.Resolve(device);
        var d = Config.HiddenDim;
        var q = Config.RelationRank;
        var r = Config.RelationTypes;
        _encoder = nn.Sequential(nn.Linear(Config.FeatureDim, 2 * q), nn.GELU(), nn.Linear(2 * q, q));
        _initial = nn.Linear(q, d);
        _update = nn.Sequential(nn.Linear(2 * d + 2 * q, 2 * d), nn.GELU(), nn.Linear(2 * d, d));
        _updateGate = nn.Linear(2 * d + q, d);
        _messageGate = nn.Linear(2 * d + q, 1);
        _router = nn.Sequential(nn.Linear(2 * d + 4 * q + 8, Config.ScoreHiddenDim),
            nn.GELU(), nn.Linear(Config.ScoreHiddenDim, 1));
        _context = nn.Linear(d + 2 * q + 4, d);
        _prefixUpdate = nn.Linear(3 * d, d);
        _intentHead = nn.Linear(d, 4);
        _editHead = nn.Linear(d, 7); // END_EDITS, NO_OP, ADD, REMOVE, RETYPE, ACTIVATE, RESET.
        _operationHead = nn.Linear(d, Config.OperationClasses);
        _portHead = nn.Linear(d, Config.PortClasses);
        _relationHead = nn.Linear(d, r);
        _stopHead = nn.Linear(d, 2);
        _bitHead = nn.Linear(d, 2);
        _typeHead = nn.Linear(d, 4);
        _lengthHead = nn.Linear(d, Config.MaximumSequenceLength + 1);
        _continueHead = nn.Linear(d, 2);
        _pointerHead = nn.Sequential(nn.Linear(2 * d + q, Config.ScoreHiddenDim),
            nn.GELU(), nn.Linear(Config.ScoreHiddenDim, 1));
        _valueHead = nn.Linear(d, 1);
        _readinessHead = nn.Linear(d, Config.PortClasses);
        _relationV = nn.Parameter(randn(r, q, d) / Math.Sqrt(d));
        _relationU = nn.Parameter(randn(r, d, q) / Math.Sqrt(q));
        _relationD = nn.Parameter(randn(r, d, q) / Math.Sqrt(q));
        _relationBias = nn.Parameter(zeros(r, d));
        _relationEmbedding = nn.Parameter(randn(r, q) * 0.1);
        _fieldEmbedding = nn.Parameter(randn(11, d) * 0.1);
        _choiceEmbedding = nn.Parameter(randn(Math.Max(128, Config.MaximumSequenceLength + 1), d) * 0.1);
        RegisterComponents();
        this.to(ScalarType.Float32);
        this.to(Device);
    }

    public override Tensor forward(Tensor features)
    {
        using var scope = NewDisposeScope();
        return InitializeHidden(EncodeLocal(features)).MoveToOuterDisposeScope();
    }

    public Tensor EncodeLocal(Tensor features)
    {
        using var scope = NewDisposeScope();
        if (features.shape.Length != 2 || features.shape[1] != Config.FeatureDim)
            throw new ArgumentException("Local features must have shape [slots,FeatureDim].");
        EnsureFinite(features, "input features");
        var encoded = _encoder.forward(features);
        EnsureFinite(encoded, "local encoder preactivation");
        return encoded.tanh().MoveToOuterDisposeScope();
    }

    public Tensor InitializeHidden(Tensor localEncoding)
    {
        using var scope = NewDisposeScope();
        var initial = _initial.forward(localEncoding);
        EnsureFinite(initial, "hidden initializer preactivation");
        return (initial.tanh() * Config.HiddenBound).MoveToOuterDisposeScope();
    }

    public Tensor EncodeGoal(Tensor goalFeatures)
    {
        using var scope = NewDisposeScope();
        return EncodeLocal(goalFeatures.reshape(1, Config.FeatureDim)).reshape(Config.RelationRank)
            .MoveToOuterDisposeScope();
    }

    public Tensor ForwardAlongEdges(Tensor hidden, Tensor localEncoding, Tensor goal,
        int[] sources, int[] targets, int[] relations, bool[]? activeMask = null)
    {
        using var scope = NewDisposeScope();
        ValidateStateAndEdges(hidden, localEncoding, goal, sources, targets, relations);
        var count = hidden.shape[0];
        var numerator = zeros(count, Config.HiddenDim, device: Device);
        var denominator = ones(count, 1, device: Device);
        for (var relation = 0; relation < Config.RelationTypes; relation++)
        {
            var positions = Enumerable.Range(0, sources.Length).Where(index => relations[index] == relation).ToArray();
            if (positions.Length == 0) continue;
            var sourceIndex = Index(positions.Select(index => sources[index]));
            var targetIndex = Index(positions.Select(index => targets[index]));
            var sourceHidden = hidden.index_select(0, sourceIndex);
            var targetHidden = hidden.index_select(0, targetIndex);
            var sourceLocal = localEncoding.index_select(0, sourceIndex);
            var message = RelationMessage(sourceHidden, sourceLocal, relation);
            var gate = _messageGate.forward(cat([sourceHidden, targetHidden, sourceLocal], 1));
            EnsureFinite(gate, "message gate preactivation");
            var weight = gate.sigmoid();
            numerator = numerator.index_add(0, targetIndex, weight * message, 1);
            denominator = denominator.index_add(0, targetIndex, weight, 1);
        }
        var updated = Update(hidden, numerator / denominator, localEncoding, goal);
        return KeepInactive(hidden, updated, activeMask).MoveToOuterDisposeScope();
    }

    /// <summary>Quadratic diagnostic reference, never used by the deployment processor.</summary>
    public Tensor DenseReference(Tensor hidden, Tensor localEncoding, Tensor goal,
        int[] sources, int[] targets, int[] relations)
    {
        using var scope = NewDisposeScope();
        ValidateStateAndEdges(hidden, localEncoding, goal, sources, targets, relations);
        var count = checked((int)hidden.shape[0]);
        var numerator = zeros(count, Config.HiddenDim, device: Device);
        var denominator = ones(count, 1, device: Device);
        var sourceIndex = Index(Enumerable.Range(0, count * count).Select(index => index % count));
        var targetIndex = Index(Enumerable.Range(0, count * count).Select(index => index / count));
        var gates = _messageGate.forward(cat([hidden.index_select(0, sourceIndex),
            hidden.index_select(0, targetIndex), localEncoding.index_select(0, sourceIndex)], 1))
            .sigmoid().reshape(count, count);
        for (var relation = 0; relation < Config.RelationTypes; relation++)
        {
            var adjacency = new float[count * count];
            for (var edge = 0; edge < sources.Length; edge++)
                if (relations[edge] == relation) adjacency[targets[edge] * count + sources[edge]] += 1;
            var weights = tensor(adjacency, device: Device).reshape(count, count) * gates;
            numerator = numerator + weights.matmul(RelationMessage(hidden, localEncoding, relation));
            denominator = denominator + weights.sum(1, keepdim: true);
        }
        return Update(hidden, numerator / denominator, localEncoding, goal).MoveToOuterDisposeScope();
    }

    public Tensor ScoreRoutes(Tensor hidden, Tensor localEncoding, Tensor goal, int[] sources,
        int[] targets, int[] relations, Tensor publicFeatures)
    {
        using var scope = NewDisposeScope();
        ValidateStateAndEdges(hidden, localEncoding, goal, sources, targets, relations);
        if (publicFeatures.shape.Length != 2 || publicFeatures.shape[0] != sources.Length || publicFeatures.shape[1] != 8)
            throw new ArgumentException("Route features must have shape [audited candidate pairs,8].");
        var sourceIndex = Index(sources);
        var targetIndex = Index(targets);
        var relationIndex = Index(relations);
        var input = cat([hidden.index_select(0, sourceIndex), hidden.index_select(0, targetIndex),
            localEncoding.index_select(0, sourceIndex), localEncoding.index_select(0, targetIndex),
            _relationEmbedding.index_select(0, relationIndex), goal.reshape(1, -1).expand(sources.Length, -1),
            publicFeatures], 1);
        var logits = _router.forward(input).reshape(-1);
        EnsureFinite(logits, "router logits");
        return logits.MoveToOuterDisposeScope();
    }

    public Tensor CreatePolicyContext(Tensor hidden, Tensor localEncoding, Tensor goal, int workerSlot, float[] budget)
    {
        using var scope = NewDisposeScope();
        if (budget.Length != 4 || workerSlot < 0 || workerSlot >= hidden.shape[0])
            throw new ArgumentException("Policy needs one audited worker and four public budget features.");
        var activation = _context.forward(cat([hidden.select(0, workerSlot).reshape(1, -1),
            localEncoding.select(0, workerSlot).reshape(1, -1), goal.reshape(1, -1),
            tensor(budget, device: Device).reshape(1, 4)], 1));
        EnsureFinite(activation, "policy context preactivation");
        var context = activation.tanh();
        EnsureFinite(context, "policy context");
        return context.MoveToOuterDisposeScope();
    }

    public Tensor FieldLogits(Tensor context, DtrPolicyField field)
    {
        using var scope = NewDisposeScope();
        var head = field switch
        {
            DtrPolicyField.Intent => _intentHead,
            DtrPolicyField.EditKind => _editHead,
            DtrPolicyField.Operation => _operationHead,
            DtrPolicyField.Port => _portHead,
            DtrPolicyField.Relation => _relationHead,
            DtrPolicyField.Stop => _stopHead,
            DtrPolicyField.Bit => _bitHead,
            DtrPolicyField.SlotType => _typeHead,
            DtrPolicyField.SequenceLength => _lengthHead,
            DtrPolicyField.ContinueIntent => _continueHead,
            _ => throw new ArgumentException("Pointers are scored over the audited candidate support.")
        };
        var logits = head.forward(context).reshape(-1);
        EnsureFinite(logits, $"{field} logits");
        return logits.MoveToOuterDisposeScope();
    }

    public Tensor PointerLogits(Tensor context, Tensor hidden, Tensor localEncoding, int[] exposedSlots)
    {
        using var scope = NewDisposeScope();
        if (exposedSlots.Length == 0 || exposedSlots.Any(slot => slot < 0 || slot >= hidden.shape[0]))
            throw new ArgumentException("Pointers require a nonempty audited candidate support.");
        var index = Index(exposedSlots);
        var logits = _pointerHead.forward(cat([context.expand(exposedSlots.Length, -1),
            hidden.index_select(0, index), localEncoding.index_select(0, index)], 1)).reshape(-1);
        EnsureFinite(logits, "pointer logits");
        return logits.MoveToOuterDisposeScope();
    }

    public Tensor AdvancePolicyContext(Tensor context, DtrPolicyField field, int choice)
    {
        using var scope = NewDisposeScope();
        // Pointer supports may exceed the embedding table: encode an identity modulo the fixed template.
        // The pointer distribution itself still covers every supplied candidate and retains its full probability.
        var embeddedChoice = Math.Abs((long)choice) % _choiceEmbedding.shape[0];
        var activation = _prefixUpdate.forward(cat([context, _fieldEmbedding.select(0, (int)field).reshape(1, -1),
            _choiceEmbedding.select(0, embeddedChoice).reshape(1, -1)], 1));
        EnsureFinite(activation, "autoregressive prefix preactivation");
        return activation.tanh().MoveToOuterDisposeScope();
    }

    public Tensor Value(Tensor context)
    {
        using var scope = NewDisposeScope();
        var value = _valueHead.forward(context).reshape([]);
        EnsureFinite(value, "value head");
        return value.MoveToOuterDisposeScope();
    }

    public Tensor ReceiptReadinessLogits(Tensor context)
    {
        using var scope = NewDisposeScope();
        var logits = _readinessHead.forward(context).reshape(-1);
        EnsureFinite(logits, "receipt readiness logits");
        return logits.MoveToOuterDisposeScope();
    }

    public void FreezeForInference()
    {
        eval();
        foreach (var parameter in parameters()) parameter.requires_grad_(false);
        IsFrozen = true;
    }

    public void BeginTraining()
    {
        if (IsFrozen) throw new InvalidOperationException("Frozen inference checkpoints cannot train. Load a separate mutable model.");
        train();
    }

    public static void EnsureFinite(Tensor value, string description)
    {
        using var scope = NewDisposeScope();
        if (!value.isfinite().all().item<bool>())
            throw new DtrNumericException($"RESOURCE_OR_NUMERIC_FAILURE: nonfinite {description}.");
    }

    private Tensor Index(IEnumerable<int> indices) => tensor(indices.Select(index => (long)index).ToArray(), device: Device);

    private Tensor RelationMessage(Tensor sourceHidden, Tensor sourceLocal, int relation)
    {
        var affine = sourceHidden.matmul(_relationV.select(0, relation).transpose(0, 1))
            .matmul(_relationU.select(0, relation).transpose(0, 1)) +
         sourceLocal.matmul(_relationD.select(0, relation).transpose(0, 1)) +
         _relationBias.select(0, relation);
        EnsureFinite(affine, "relation message preactivation");
        return affine.tanh() * Config.HiddenBound;
    }

    private Tensor Update(Tensor hidden, Tensor message, Tensor localEncoding, Tensor goal)
    {
        var affine = _update.forward(cat([hidden, message, localEncoding,
            goal.reshape(1, -1).expand(hidden.shape[0], -1)], 1));
        EnsureFinite(affine, "recurrent update preactivation");
        var candidate = affine.tanh() * Config.HiddenBound;
        var gate = _updateGate.forward(cat([hidden, message, localEncoding], 1));
        EnsureFinite(gate, "recurrent update gate preactivation");
        var alpha = gate.sigmoid();
        var result = (1 - alpha) * hidden + alpha * candidate;
        EnsureFinite(result, "recurrent hidden state");
        if (result.abs().max().item<float>() > Config.HiddenBound + 1e-5f)
            throw new DtrNumericException("Hidden state escaped the configured convex-update bound.");
        return result;
    }

    private Tensor KeepInactive(Tensor hidden, Tensor updated, bool[]? activeMask)
    {
        if (activeMask is null) return updated;
        if (activeMask.LongLength != hidden.shape[0]) throw new ArgumentException("Active mask must cover every slot row.");
        var mask = tensor(activeMask.Select(active => active ? 1f : 0f).ToArray(), device: Device).reshape(-1, 1);
        return mask * updated + (1 - mask) * hidden;
    }

    private void ValidateStateAndEdges(Tensor hidden, Tensor localEncoding, Tensor goal,
        int[] sources, int[] targets, int[] relations)
    {
        if (hidden.shape.Length != 2 || hidden.shape[1] != Config.HiddenDim ||
            localEncoding.shape.Length != 2 || localEncoding.shape[0] != hidden.shape[0] ||
            localEncoding.shape[1] != Config.RelationRank || goal.numel() != Config.RelationRank ||
            sources.Length != targets.Length || sources.Length != relations.Length ||
            sources.Concat(targets).Any(index => index < 0 || index >= hidden.shape[0]) ||
            relations.Any(relation => relation < 0 || relation >= Config.RelationTypes))
            throw new ArgumentException("Invalid hidden/local/goal shapes or hard edge indices.");
        EnsureFinite(hidden, "prior hidden state");
        EnsureFinite(localEncoding, "local encoding");
        EnsureFinite(goal, "goal encoding");
        if (hidden.numel() > 0 && hidden.abs().max().item<float>() > Config.HiddenBound + 1e-5f)
            throw new DtrNumericException("Prior hidden state is outside the configured bound.");
    }
}
