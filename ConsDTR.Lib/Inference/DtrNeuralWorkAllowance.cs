using System.Text;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;

namespace ConsDTR.Lib.Inference;

/// <summary>Declared finite scalar-operation allowances, independent of parameter values. These are not measured device FLOPs.</summary>
internal static class DtrNeuralWorkAllowance
{
    public static void Charge(RuntimeSession runtime, RuntimeSnapshot snapshot, DtrModelConfig config,
        DirectoryOptions directory, bool includePolicy, int? exposedCount = null, int? routePairCount = null)
    {
        try
        {
            checked
            {
                long d = config.HiddenDim, q = config.RelationRank, f = config.FeatureDim, s = config.ScoreHiddenDim;
                long p = config.PortClasses, o = config.OperationClasses, l = config.MaximumSequenceLength;
                long n = snapshot.Slots.Count, e = Math.Min(snapshot.Edges.Count + (long)runtime.Options.MaxEdgeEditsPerStep,
                    n * Math.Min(runtime.Options.MaxInDegree, runtime.Options.MaxOutDegree));
                long decisions = runtime.Options.MaxEdgeEditsPerStep + (long)runtime.Options.MaxSlotOperationsPerStep;
                long exposed = Math.Min(n, Math.Min(directory.MaxTargetsPerStep * ((long)directory.CandidatesPerTarget + 1), runtime.Options.MaxControlReadsPerStep));
                if (exposedCount is { } actualExposed) exposed = Math.Min(exposed, actualExposed);
                long k = Math.Min(runtime.Options.MaxInDegree, p), templatesPerPair = 2 * k + 3;
                long pairs = routePairCount ?? directory.MaxTargetsPerStep * (long)directory.CandidatesPerTarget;
                long routes = pairs * templatesPerPair;
                long removals = Math.Min(e, exposed * Math.Min(runtime.Options.MaxInDegree, runtime.Options.MaxOutDegree));
                long retypes = removals * templatesPerPair, endpoints = Math.Min(runtime.Options.MaxEditedEndpointsPerStep, 2 * decisions);
                long encode = 2 * f * q + 2 * q * q, initialize = d * q;
                long update = 8 * d * d + 5 * d * q, message = 3 * d * q + 2 * d + q;
                long context = d * (d + 2 * q + 4), prefix = 3 * d * d;
                long route = s * (2 * d + 4 * q + 8) + s, pointer = s * (2 * d + q) + s;
                long mac = 2 * n * (encode + initialize) + n * update + e * message;
                long scalar = 64 * (mac + 2 * n * (d + q + 1) + config.RelationTypes * e);
                long cpu = n + e;
                if (includePolicy)
                {
                    long editor = decisions * (7 * d + 2 * prefix + Math.Max(Math.Max(routes * route, retypes * route),
                        Math.Max(removals * route, exposed * pointer))) + 7 * d;
                    long intent = 4 * context + 3 * exposed * pointer + (o + 4) * d + 4 * prefix;
                    long action = o * d + (l + 1) * d + (k + 1) * p * d + 2 * d + (k + 3) * prefix;
                    long count = Math.Min(exposed, l);
                    long sequence = (l + 1) * d + (count + 1) * prefix + count * exposed * pointer;
                    long bits = 64 * (2 * d + prefix);
                    long candidate = Math.Max(bits, sequence + count * (bits + 2 * prefix));
                    // Every edge option is scored with source, target, role and public port features.
                    mac = editor + intent + action + candidate;
                    scalar += 64 * (mac + (decisions + 4) * n * (d + q + 1));
                    // Preview uses degree/port/edge indices and templates grouped by endpoint pair.
                    // Include hash/support construction, finite metadata searches, selection and sorting.
                    cpu += 64 * ((decisions + 1) * (n + e + routes + retypes + removals) * (endpoints + 2) * (endpoints + 2)
                        + count * n * (count + 1) + exposed * exposed + count * l);
                }
                var bytes = 2 * snapshot.Records.Sum(record => (long)Encoding.UTF8.GetByteCount(record.Value.Data) +
                    Encoding.UTF8.GetByteCount(record.Value.Type) + Encoding.UTF8.GetByteCount(record.InputId)) + n * 128;
                var allowance = scalar + cpu;
                runtime.RecordAudit("NEURAL_STEP_ALLOWANCE", new
                {
                    Schedule = "ConsDTRv1-NeuralScalarAllowance/v2", TensorScalarAllowance = scalar,
                    ControllerScalarAllowance = cpu, EncodedBytes = bytes, PolicyIncluded = includePolicy,
                    MaximumCandidateItems = Math.Min(exposed, l)
                }, 1 + (allowance + 1023) / 1024 + bytes);
            }
        }
        catch (OverflowException) { throw new RuntimeResourceException("Neural/controller work allowance exceeds the supported finite counter."); }
    }
}
