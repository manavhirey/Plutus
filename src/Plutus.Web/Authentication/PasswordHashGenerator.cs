using Microsoft.AspNetCore.Identity;

namespace Plutus.Web.Authentication;

internal static class PasswordHashGenerator
{
    internal const string Command = "--create-password-hash";
    internal const int MinimumPasswordLength = 16;

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == Command;

    public static void Run(TextReader input, TextWriter output)
    {
        output.Write("New Plutus administrator password: ");
        var password = ReadPassword(input, output);
        output.Write("Confirm password: ");
        var confirmation = ReadPassword(input, output);

        if (password.Length < MinimumPasswordLength)
        {
            throw new InvalidOperationException($"Use an administrator password of at least {MinimumPasswordLength} characters.");
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The passwords did not match.");
        }

        var hash = new PasswordHasher<object>().HashPassword(new object(), password);
        output.WriteLine();
        output.WriteLine("Add this single line to the protected deployment environment:");
        output.WriteLine($"{PlutusAuthentication.PasswordHashEnvironmentVariable}={hash}");
    }

    private static string ReadPassword(TextReader input, TextWriter output)
    {
        if (!ReferenceEquals(input, Console.In))
        {
            return input.ReadLine() ?? string.Empty;
        }

        var characters = new List<char>();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }

        output.WriteLine();
        return new string(characters.ToArray());
    }
}
