using System.Diagnostics;
using System.Globalization;
using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.Lib.Evaluation;

/// <summary>Sequential, frozen-model evaluation with separately labeled learned, fallback and diagnostic outcomes.</summary>
public sealed class DtrEvaluationRunner(DtrNeuralModel model)
{
    public DtrEvaluationReport Evaluate(IEnumerable<DtrEvaluationCase> cases, DtrEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(cases); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Inference); ArgumentNullException.ThrowIfNull(options.Inference.Runtime);
        ArgumentNullException.ThrowIfNull(options.Inference.Directory); ArgumentNullException.ThrowIfNull(options.Bootstrap);
        if (options.MaximumCases is < 1 or > 1_000_000 || string.IsNullOrWhiteSpace(options.DataSplitFingerprint)
            || options.DataSplitFingerprint.Length > 256 || options.CallerDeclaredTrainingSeeds is null
            || options.CallerDeclaredTrainingSeeds.Distinct().Count() != options.CallerDeclaredTrainingSeeds.Count
            || !double.IsFinite(options.Inference.Exploration) || options.Inference.Exploration is < 0 or > 1)
            throw new ArgumentException("Evaluation requires a declared data-split fingerprint, finite case limit and nonduplicate caller training-seed metadata.");
        options.Bootstrap.Validate(); options.Inference.Directory.Validate();
        var samples = cases.Take(checked(options.MaximumCases + 1)).ToArray();
        if (samples.Length > options.MaximumCases) throw new RuntimeResourceException("Evaluation case count exceeds the declared cap.");
        foreach (var sample in samples)
        {
            if (sample is null || sample.Task is null || string.IsNullOrWhiteSpace(sample.Split) || sample.Split.Length > 128
                || string.IsNullOrWhiteSpace(sample.Family) || sample.Family.Length > 256
                || sample.Scale < 1 || sample.IsSatisfiable.HasValue && sample.Task.Definition.SemanticVersion != FiniteCspTask.Semantics)
                throw new ArgumentException("Every evaluation case must identify its task, split, family and scale; SAT labels belong only to FiniteCSP.");
        }
        var mode = options.Inference.Runtime.Mode;
        if (!Enum.IsDefined(mode) || mode == ExecutionMode.LearnedOnly && options.ReferencePlanFactory is not null
            || mode == ExecutionMode.OracleDiagnostic && options.ReferencePlanFactory is null
            || mode == ExecutionMode.FairFallback && options.ReferencePlanFactory is null && samples.Any(sample => sample.Task is not HornTask))
            throw new ArgumentException("Learned evaluation excludes reference plans; diagnostics need explicit plans, and default fair fallback covers finite Horn only.");
        if (mode != ExecutionMode.LearnedOnly && (options.Inference.Editor != DtrEditorMode.Learned || options.Inference.Messages != DtrMessageMode.Enabled))
            throw new ArgumentException("Reference-plan execution does not apply learned-policy editor/message interventions.");
        if (samples.Length > 1 && options.Inference.Runtime.JournalPath is not null && options.JournalPathFactory is null)
            throw new ArgumentException("A multi-case persisted evaluation needs an explicit distinct JournalPathFactory.");

        model.FreezeForInference();
        var checkpoint = DtrCheckpointStore.ComputeModelDigest(model);
        var identities = samples.Select((sample, index) => new
        {
            Index = index, DefinitionDigest = DtrCanonical.Digest(sample.Task.Definition), sample.Split,
            sample.Family, sample.Scale, sample.StructureSeed, OracleIsSatisfiable = sample.IsSatisfiable
        }).ToArray();
        var corpus = DtrCanonical.Digest(identities);
        // Session identities and storage paths differ per run and do not define the common resource budget.
        var budget = options.Inference.Runtime with { SessionId = Guid.Empty, JournalPath = null, ModelCheckpointDigest = "bound-by-report-checkpoint" };
        const string costSchedule = "ConsDTRv1-DeterministicWork/v2:Journal32+CanonicalBytes/64;NeuralScalarAllowance/v2/1024+EncodedBytes";
        const string samplingSchedule = "base + caseIndex modulo 2^64";
        var budgetSignature = DtrCanonical.Digest(new
        {
            Budget = budget, Directory = options.Inference.Directory, CostSchedule = costSchedule,
            options.Inference.Deterministic, SamplingSeed = options.Inference.SamplingSeed.ToString(CultureInfo.InvariantCulture),
            SamplingSeedSchedule = samplingSchedule, Exploration = options.Inference.Exploration.ToString("R", CultureInfo.InvariantCulture)
        });
        var metadata = new DtrEvaluationMetadata("ConsDTRv1", "Draft-2", options.DataSplitFingerprint, corpus,
            options.CheckpointId, checkpoint, 1, Array.AsReadOnly(options.CallerDeclaredTrainingSeeds.ToArray()),
            "Caller declared; the evaluator does not infer or validate the number of independent training runs.", costSchedule,
            budget, options.Inference.Directory, budgetSignature, options.Bootstrap, options.Inference.Editor,
            options.Inference.Messages, options.Inference.Deterministic, options.Inference.SamplingSeed,
            samplingSchedule, options.Inference.Exploration, mode == ExecutionMode.LearnedOnly,
            "Measured frozen-checkpoint execution. Latency covers the per-case inference entrypoint. Editor/message settings apply only to LearnedOnly and do not alone establish capability or architecture benefit. Wilson intervals assume independent binary instances; paired bootstrap assumes independently sampled instance pairs.");
        var rows = new List<DtrEvaluationCaseResult>(samples.Length);
        var journalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var learned = new DtrInferenceRunner(model); var reference = new DtrTaskPlanExecutor(model);
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index]; var identity = identities[index];
            var seed = unchecked(options.Inference.SamplingSeed + (ulong)index);
            var path = options.JournalPathFactory?.Invoke(sample, index) ?? options.Inference.Runtime.JournalPath;
            if (path is not null && !journalPaths.Add(Path.GetFullPath(path))) throw new ArgumentException("Persisted evaluation cases must use distinct WAL paths.");
            var inference = options.Inference with
            {
                SamplingSeed = seed,
                Runtime = options.Inference.Runtime with { SessionId = Guid.NewGuid(), JournalPath = path, ModelCheckpointDigest = checkpoint }
            };
            DtrInferenceResult run;
            var runClock = Stopwatch.StartNew();
            if (mode == ExecutionMode.LearnedOnly) run = learned.Run(sample.Task, inference);
            else if (options.ReferencePlanFactory is not null)
                run = reference.Execute(sample.Task, charge => options.ReferencePlanFactory(sample, charge), inference.Runtime);
            else run = reference.RunHornFallback(((HornTask)sample.Task).Problem, inference.Runtime);
            runClock.Stop();
            if (run.Mode != mode || run.Snapshot.Mode != mode) throw new InvalidDataException("Evaluation run mode differs from the declared cohort.");
            if (DtrCanonical.Digest(sample.Task.Definition) != identity.DefinitionDigest)
                throw new InvalidDataException("Evaluation task changed during frozen-input inference.");
            var verificationClock = Stopwatch.StartNew();
            VerificationReport? verification = null;
            if (run.Certificate is not null)
            {
                if (DtrCanonical.Digest(run.Certificate.Task) != identity.DefinitionDigest
                    || run.Certificate.Manifest.ModelCheckpointDigest != checkpoint)
                    throw new InvalidDataException("Evaluation certificate is bound to another task or checkpoint.");
                verification = IndependentVerifier.Verify(run.Certificate, sample.Task.CreateIndependentVerifier(),
                    run.ExpectedEndAnchor, inference.Runtime.FinalGateWorkAllowance);
            }
            verificationClock.Stop();
            var certified = run.IsVerified && run.Verification?.Accepted == true && verification?.Accepted == true;
            if (certified && (verification!.IsNegative != (run.Status == DtrResultStatus.VerifiedNegative)))
                throw new InvalidDataException("Evaluation output status contradicts its independently checked predicate.");
            var work = run.Certificate?.Manifest.DeclaredWorkCost ?? checked(inference.Runtime.MaxWork - run.Snapshot.RemainingWork);
            var journalBytes = run.Snapshot.JournalBytes;
            if (run.Certificate is not null && run.Certificate.RuntimeJournal.Sum(entry => DtrCanonical.Encode(entry).LongLength) != journalBytes)
                throw new InvalidDataException("Journal byte telemetry differs from the independently exported complete prefix.");
            var status = run.IsVerified && !certified ? DtrResultStatus.ResourceOrNumericFailure : run.Status;
            rows.Add(new(index.ToString("D8", CultureInfo.InvariantCulture) + ":" + identity.DefinitionDigest,
                run.Snapshot.SessionId, sample.Task.Definition.TaskId, sample.Task.Definition.SemanticVersion,
                identity.DefinitionDigest, sample.Split, sample.Family, sample.Scale, sample.StructureSeed,
                mode, status, certified, sample.IsSatisfiable, verification is { Accepted: false } ? verification.Reason : run.Reason,
                checked(inference.Runtime.MaxMicrosteps - run.Snapshot.RemainingMicrosteps), work,
                run.Exposure?.ScannedEntries, run.AcceptedEdits, run.RejectedActions, run.Snapshot.Proofs.Count,
                journalBytes, runClock.Elapsed.TotalMilliseconds, run.Certificate is null ? 0 : verificationClock.Elapsed.TotalMilliseconds,
                verification?.VerificationWork ?? 0, run.Certificate is null ? null : DtrCanonical.Digest(run.Certificate), seed, run.Snapshot.JournalEvents));
        }
        if (DtrCheckpointStore.ComputeModelDigest(model) != checkpoint)
            throw new InvalidDataException("Model parameters changed during a frozen-checkpoint evaluation.");
        var readOnly = Array.AsReadOnly(rows.ToArray());
        return new(metadata, readOnly, DtrEvaluationAggregator.Summarize(readOnly), DtrEvaluationAggregator.Summarize(readOnly, overallByMode: true));
    }
}
