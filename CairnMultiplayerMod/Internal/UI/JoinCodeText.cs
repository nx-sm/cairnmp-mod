using System;

namespace CairnMultiplayerMod.Internal.UI;

/// <summary>
/// Turns whatever was copied into something the join field can use. Codes and addresses are
/// usually passed around in a chat message — "join me: 100.64.1.2:27420" — so the address is
/// picked out of the text rather than the whole line pasted in.
/// </summary>
internal static class JoinCodeText
{
    private static readonly char[] Separators = { ' ', '\t', '\r', '\n' };
    private static readonly char[] Wrapping = { '"', '\'', '`', ',', ';', '(', ')', '<', '>' };

    /// <returns>The code or address to put in the field, or an empty string.</returns>
    internal static string FromClipboard(string clipboard, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(clipboard) || maxLength <= 0) return "";

        string first = null;
        foreach (var raw in clipboard.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            // A trailing full stop ends a sentence, not an address — and may sit outside a
            // closing quote or inside it.
            var token = raw.Trim(Wrapping).TrimEnd('.').Trim(Wrapping).TrimEnd('.');
            // "code:", "join me:" — a label, not the thing itself.
            if (token.Length == 0 || token.EndsWith(":")) continue;
            first ??= token;
            // An address has dots or a port; a lobby code has neither.
            if (token.IndexOf('.') >= 0 || token.IndexOf(':') >= 0)
                return Limit(token, maxLength);
        }
        return first == null ? "" : Limit(first, maxLength);
    }

    private static string Limit(string text, int maxLength)
        => text.Length <= maxLength ? text : text.Substring(0, maxLength);
}
