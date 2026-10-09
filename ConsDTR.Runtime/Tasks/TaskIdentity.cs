using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

internal static class TaskIdentity
{
    public static string Require(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 128)
            throw new ArgumentException("Task identities are limited to 128 characters.", parameter);
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                    throw new ArgumentException("Task identities require valid Unicode scalar sequences.", parameter);
            }
            else if (char.IsLowSurrogate(value[index]))
                throw new ArgumentException("Task identities require valid Unicode scalar sequences.", parameter);
        }
        return value;
    }

    public static ImmutableArray<string> Set(IEnumerable<string> values, string parameter)
    {
        ArgumentNullException.ThrowIfNull(values, parameter);
        return values.Select(value => Require(value, parameter)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public static void RequireDistinct(IEnumerable<string> values, string parameter)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(Require(value, parameter)))
                throw new ArgumentException($"Duplicate identity '{value}'.", parameter);
    }
}
