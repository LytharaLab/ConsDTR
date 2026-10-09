using ConsDTR.Lib.Models;
using TorchSharp;

namespace ConsDTR.Lib.Training;

/// <summary>Live likelihood tensors must come from the same on-policy hard runtime trajectory.</summary>
public sealed record DtrRolloutStep(DtrPolicyTrace Policy, torch.Tensor ValueBeforeAction,
    double Reward, bool Terminal, long StateVersion, bool BpttBoundary = false);
