using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.Tando.Helper;

public class KenyanPhoneNumber
{
    private static readonly Regex Msisdn = new(@"^(?:\+?254|0)([17]\d{8})$", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[\s\-]", RegexOptions.Compiled);

    public const string ExampleFormat = "0712345678 or +254712345678";

    public static string? Normalize(string? phoneNumber)
    {
        var compact = Separators.Replace(phoneNumber ?? string.Empty, "");
        var match = Msisdn.Match(compact);
        return match.Success ? "254" + match.Groups[1].Value : null;
    }
}