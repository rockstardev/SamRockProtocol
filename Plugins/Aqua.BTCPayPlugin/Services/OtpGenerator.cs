using System.Linq;
using System.Security.Cryptography;

public class OtpGenerator
{
    private static readonly char[] AllowedChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    public static string Generate(int length = 21)
    {
        byte[] randomBytes = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        return new string(randomBytes.Select(b => AllowedChars[b % AllowedChars.Length]).ToArray());
    }
}