namespace ConsDTR.Lib.Evaluation;

public sealed record DtrConfidenceInterval(double Lower, double Upper, double ConfidenceLevel,
    string Method, int Observations);
