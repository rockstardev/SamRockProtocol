using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SamRockProtocol.Services;

/// <summary>
/// Output descriptor parsing helpers for SamRock protocol. Pure static functions
/// over input strings - no DI dependencies - so they can be unit-tested directly
/// without the BTCPay test stack.
/// </summary>
public static class DescriptorParser
{
    /// <summary>
    /// Strips all whitespace from a descriptor. Output descriptors have no
    /// legitimate internal whitespace; this normalizes wallets that emit
    /// stray whitespace around the body or checksum.
    /// </summary>
    public static string NormalizeDescriptor(string descriptor)
    {
        if (descriptor == null)
            return null;
        return Regex.Replace(descriptor, @"\s+", string.Empty);
    }

    /// <summary>
    /// Converts h-style hardened markers ("84h/0h/0h") to apostrophe form
    /// ("84'/0'/0'") component-by-component. Handles both lowercase "h" and
    /// uppercase "H". Components already in apostrophe form are untouched.
    /// </summary>
    public static string NormalizeDerivationPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        return string.Join('/', path.Split('/').Select(component =>
            component.EndsWith("h", StringComparison.OrdinalIgnoreCase)
                ? component[..^1] + "'"
                : component));
    }

    // BTC descriptor: wpkh / pkh / sh / tr enclosing a [fingerprint/path]xpub.../range
    // followed by an optional #checksum. Anchored at both ends to reject
    // malformed trailing input.
    private static readonly Regex BtcDescriptorRegex = new(
        @"^(\w+)\(\[([a-fA-F0-9]{8})/([^\]]+)\](xpub[^/\)]+)(/[^\)]+)?\)(?:#[a-zA-Z0-9]+)?$",
        RegexOptions.Compiled);

    // LBTC native: ct(slip77(blinding),elwpkh([fingerprint/path]xpub.../range))
    private static readonly Regex LbtcNativeRegex = new(
        @"^ct\(slip77\(([a-fA-F0-9]{64})\),elwpkh\(\[([a-fA-F0-9]{8})/([^\]]+)\](xpub[^/\)]+)(/[^\)]+)?\)\)(?:#[a-zA-Z0-9]+)?$",
        RegexOptions.Compiled);

    // LBTC wrapped: ct(slip77(blinding),elsh(<innerType>([fingerprint/path]xpub.../range)))
    private static readonly Regex LbtcWrappedRegex = new(
        @"^ct\(slip77\(([a-fA-F0-9]{64})\),elsh\((\w+)\(\[([a-fA-F0-9]{8})/([^\]]+)\](xpub[^/\)]+)(/[^\)]+)?\)\)\)(?:#[a-zA-Z0-9]+)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a BTC output descriptor. On success, returns true and populates
    /// the out fields. On failure, returns false and sets <paramref name="error"/>.
    /// Derivation path is normalized to apostrophe form.
    /// </summary>
    public static bool TryParseBitcoinDescriptor(string descriptor,
        out string scriptType, out string fingerprint, out string derivationPath, out string xpub, out string error)
    {
        scriptType = null;
        fingerprint = null;
        derivationPath = null;
        xpub = null;
        error = null;

        if (string.IsNullOrWhiteSpace(descriptor))
        {
            error = "Invalid BTC descriptor format - descriptor is empty.";
            return false;
        }

        var match = BtcDescriptorRegex.Match(descriptor);
        if (!match.Success)
        {
            error = "Invalid BTC descriptor format - could not parse script type, fingerprint, derivation path, and xpub.";
            return false;
        }

        scriptType = match.Groups[1].Value;
        fingerprint = match.Groups[2].Value;
        derivationPath = NormalizeDerivationPath(match.Groups[3].Value);
        xpub = match.Groups[4].Value;
        return true;
    }

    /// <summary>
    /// Parses a Liquid output descriptor in either native form
    /// (ct(slip77(...),elwpkh(...))) or wrapped form
    /// (ct(slip77(...),elsh(wpkh(...)))). On success, returns true and
    /// populates the out fields including the NBXplorer suffix
    /// ("" for native, "-[p2sh]" for wrapped). Only wpkh is supported inside
    /// the elsh wrapper - other inner script types are rejected with an error.
    /// </summary>
    public static bool TryParseLiquidDescriptor(string descriptor,
        out string blindingKey, out string suffix, out string fingerprint,
        out string derivationPath, out string xpub, out string error)
    {
        blindingKey = null;
        suffix = null;
        fingerprint = null;
        derivationPath = null;
        xpub = null;
        error = null;

        if (string.IsNullOrWhiteSpace(descriptor))
        {
            error = "Invalid LBTC descriptor format - descriptor is empty.";
            return false;
        }

        var nativeMatch = LbtcNativeRegex.Match(descriptor);
        if (nativeMatch.Success)
        {
            blindingKey = nativeMatch.Groups[1].Value;
            fingerprint = nativeMatch.Groups[2].Value;
            derivationPath = NormalizeDerivationPath(nativeMatch.Groups[3].Value);
            xpub = nativeMatch.Groups[4].Value;
            suffix = "";
            return true;
        }

        var wrappedMatch = LbtcWrappedRegex.Match(descriptor);
        if (wrappedMatch.Success)
        {
            var scriptType = wrappedMatch.Groups[2].Value;
            if (!string.Equals(scriptType, "wpkh", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unsupported LBTC script type: elsh({scriptType})";
                return false;
            }

            blindingKey = wrappedMatch.Groups[1].Value;
            fingerprint = wrappedMatch.Groups[3].Value;
            derivationPath = NormalizeDerivationPath(wrappedMatch.Groups[4].Value);
            xpub = wrappedMatch.Groups[5].Value;
            suffix = "-[p2sh]";
            return true;
        }

        error = "Invalid LBTC descriptor format - expected ct(slip77(...),elwpkh(...)) or ct(slip77(...),elsh(wpkh(...))).";
        return false;
    }
}
