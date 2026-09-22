using System;
using System.Text;
using HydraDeobfuscator.Core;

namespace HydraDeobfuscator.Strings;

internal static class CaesarCipher
{
    public static string DecryptModule(string input, int key)
    {
        var output = new StringBuilder(input.Length);

        foreach (char c in input)
            output.Append((char)(c - key));

        return output.ToString();
    }

    public static string DecryptCombined(string input, int key2, DeobfuscationContext context)
    {
        string tmp = DecryptModule(input, key2);

        try
        {
            var data = Convert.FromBase64String(tmp);

            for (int i = 0; i < data.Length; i++)
                data[i] ^= context.HailKey[i % context.HailKey.Length];

            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return tmp;
        }
    }

    public static string? TryBase64Xor(string input, DeobfuscationContext context)
    {
        try
        {
            var data = Convert.FromBase64String(input);

            for (int i = 0; i < data.Length; i++)
                data[i] ^= context.HailKey[i % context.HailKey.Length];

            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }
}
