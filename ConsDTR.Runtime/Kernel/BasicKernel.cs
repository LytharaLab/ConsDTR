using System.Globalization;
using System.Numerics;

namespace ConsDTR.Runtime;

internal static class BasicKernel
{
    public static bool Supports(string operation) => operation is "INPUT_REF" or "COPY" or "EQ" or "COMPARE" or "ADD" or "SUB" or "SELECT" or "PROPOSE_VALUE";

    public static KernelCheck Check(KernelAction action, KernelReadView view, SessionOptions options)
    {
        try
        {
            var values = view.Operands.Select(x => x.Value).ToArray();
            void Arity(int count) { if (values.Length != count) throw new InvalidDataException("Wrong basic-operation arity."); }
            var trust = values.Any(x => x.Trust == ValueTrust.Candidate) ? ValueTrust.Candidate : ValueTrust.Verified;
            BigInteger Integer(ExactValue value)
            {
                if (value.Type != "int" || !BigInteger.TryParse(value.Data, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
                    || result.ToString(CultureInfo.InvariantCulture) != value.Data || !Fits(result))
                    throw new InvalidDataException("Noncanonical or oversized exact integer.");
                return result;
            }
            bool Fits(BigInteger value) => value >= -(BigInteger.One << (options.MaxIntegerBits - 1)) && value < (BigInteger.One << (options.MaxIntegerBits - 1));
            switch (action.Operation)
            {
                case "INPUT_REF":
                    Arity(1);
                    if (view.Operands[0].InputId.Length == 0) return KernelCheck.Reject("INPUT_REF requires a transported immutable input.");
                    return KernelCheck.Accept(values[0], view.Operands[0].InputId);
                case "COPY": Arity(1); return KernelCheck.Accept(values[0], view.Operands[0].InputId.Length == 0 ? null : view.Operands[0].InputId);
                case "PROPOSE_VALUE":
                    Arity(0);
                    return action.Candidate is null ? KernelCheck.Reject("No candidate value.") : KernelCheck.Accept(action.Candidate with { Trust = ValueTrust.Candidate });
                case "EQ":
                    Arity(2);
                    if (values[0].Type != values[1].Type) return KernelCheck.Reject("EQ requires identical exact types.");
                    return KernelCheck.Accept(new("bool", values[0].Data == values[1].Data ? "true" : "false", trust));
                case "COMPARE": case "ADD": case "SUB":
                    Arity(2);
                    var a = Integer(values[0]); var b = Integer(values[1]);
                    var number = action.Operation switch { "ADD" => a + b, "SUB" => a - b, _ => new BigInteger(a.CompareTo(b)) };
                    if (!Fits(number)) return KernelCheck.Reject("Exact integer overflow/capacity exceeded.");
                    return KernelCheck.Accept(new("int", number.ToString(CultureInfo.InvariantCulture), trust));
                case "SELECT":
                    Arity(3);
                    if (values[0].Type != "bool" || values[0].Trust != ValueTrust.Verified || values[0].Data is not ("true" or "false")
                        || values[1].Type != values[2].Type || values[1].Trust != values[2].Trust) return KernelCheck.Reject("Invalid SELECT types/trust.");
                    var selected = values[0].Data == "true" ? 1 : 2;
                    return KernelCheck.Accept(values[selected], view.Operands[selected].InputId.Length == 0 ? null : view.Operands[selected].InputId);
                default: return KernelCheck.Reject("Unsupported basic operation.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
        { return KernelCheck.Reject(error.Message); }
    }
}
