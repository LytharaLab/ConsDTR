using System.Security.Cryptography;
using System.Text;

namespace ConsDTR.Runtime.Tasks;

internal static class TaskJson
{
    public static string Write<T>(T value) => DtrCanonical.Json(value);

    public static T Read<T>(string value) => DtrCanonical.Parse<T>(value);

    public static string Fingerprint(string input, string query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input + "\n" + query)));
}
