using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

public sealed record DtrEvaluationGroup(ExecutionMode Mode, string TaskSemanticVersion,
    string Split, string Family, int Scale, int Instances, int CertifiedInstances,
    double CertifiedCoverage, DtrConfidenceInterval CertifiedCoverage95,
    int UnknownInstances, double UnknownFraction, int ResourceOrNumericFailures,
    int UnsupportedInstances, int UnverifiedCandidates, int KnownSatInstances,
    int SatWitnesses, double? SatWitnessCoverage, DtrConfidenceInterval? SatWitnessCoverage95,
    int UnlabeledCspInstances, int OracleLabelConflicts, long TotalMicrosteps, long TotalWork,
    long KnownDirectoryScannedEntries, int DirectoryMeasurements, long EditedEdges,
    long RejectedActions, long TotalProofs, long KnownJournalBytes, int JournalMeasurements,
    double MeanRunMilliseconds, double MedianRunMilliseconds, double P95RunMilliseconds,
    double ExternalEvaluationVerificationMilliseconds, long ExternalEvaluationVerificationWork, long TotalJournalEvents);
