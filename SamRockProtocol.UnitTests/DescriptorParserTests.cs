using SamRockProtocol.Services;
using Xunit;

namespace SamRockProtocol.UnitTests;

/// <summary>
/// Pure unit tests for SamRockProtocol's descriptor parsing helpers. No DI,
/// no BTCPay test stack - parses string inputs and inspects outputs directly.
/// Covers the AQUA happy path, the BULL wallet additions from PR #10, and
/// the input-hardening edge cases from PR #11.
/// </summary>
public class DescriptorParserTests
{
    // Reference descriptors copied verbatim from real wallet emissions in
    // production logs. Same e17c2d80 fingerprint thread used across
    // happy-path and parser tests for cross-test traceability.
    private const string AquaBtc =
        "wpkh([e17c2d80/84'/0'/0']xpub6BemYiVNp19a19pfjF1QyNfD9vWnUYcZFgqo1m2cRP7GJJ7j9QZKuEGHnP775g4dFWFBm1h9jDGzqoK617XnyamAcLATGaAC68Cm5sgVS1V/0/*)#sutkjd48";

    private const string AquaLbtcWrapped =
        "ct(slip77(c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403),elsh(wpkh([e17c2d80/49'/1776'/0']xpub6BemYiVNp19a2CyepSKDsDp2LgfvzZHvmepc5yM656fFDf93qcZ8UpgNwK9EwNbBimkr4mjNbK7anPqKS9M3pa9sGtve9seQaHuQJjJU6ps/0/*)))#ugh3xr7l";

    private const string BullLbtcNative =
        "ct(slip77(c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403),elwpkh([e17c2d80/84h/1776h/0h]xpub6BemYiVNp19a2CyepSKDsDp2LgfvzZHvmepc5yM656fFDf93qcZ8UpgNwK9EwNbBimkr4mjNbK7anPqKS9M3pa9sGtve9seQaHuQJjJU6ps/0/*))#abc12345";

    // ---- NormalizeDescriptor ----

    [Theory]
    [InlineData("wpkh([e17c2d80/84'/0'/0']xpub.../0/*)#abc", "wpkh([e17c2d80/84'/0'/0']xpub.../0/*)#abc")]
    [InlineData(" wpkh ( foo ) ", "wpkh(foo)")]
    [InlineData("wpkh(\n  foo\n)\n#chk", "wpkh(foo)#chk")]
    [InlineData("", "")]
    public void NormalizeDescriptor_StripsAllWhitespace(string input, string expected)
    {
        Assert.Equal(expected, DescriptorParser.NormalizeDescriptor(input));
    }

    [Fact]
    public void NormalizeDescriptor_NullPassesThrough()
    {
        Assert.Null(DescriptorParser.NormalizeDescriptor(null));
    }

    // ---- NormalizeDerivationPath ----

    [Theory]
    [InlineData("84'/0'/0'", "84'/0'/0'")]   // already apostrophe form, untouched
    [InlineData("84h/0h/0h", "84'/0'/0'")]   // lowercase h normalized
    [InlineData("84H/0H/0H", "84'/0'/0'")]   // uppercase H normalized
    [InlineData("84h/0'/0", "84'/0'/0")]     // mixed (h, ', no-suffix) handled per-component
    [InlineData("m/84h/0h", "m/84'/0'")]     // leading m preserved
    [InlineData("0", "0")]                    // single non-hardened component
    [InlineData("", "")]
    public void NormalizeDerivationPath_ConvertsHtoApostrophe(string input, string expected)
    {
        Assert.Equal(expected, DescriptorParser.NormalizeDerivationPath(input));
    }

    [Fact]
    public void NormalizeDerivationPath_NullPassesThrough()
    {
        Assert.Null(DescriptorParser.NormalizeDerivationPath(null));
    }

    // ---- TryParseBitcoinDescriptor ----

    [Fact]
    public void TryParseBitcoinDescriptor_AquaWpkh_Parses()
    {
        Assert.True(DescriptorParser.TryParseBitcoinDescriptor(AquaBtc,
            out var scriptType, out var fingerprint, out var derivationPath, out var xpub, out var error));
        Assert.Null(error);
        Assert.Equal("wpkh", scriptType);
        Assert.Equal("e17c2d80", fingerprint);
        Assert.Equal("84'/0'/0'", derivationPath);
        Assert.StartsWith("xpub6BemYiVNp19a1", xpub);
    }

    [Fact]
    public void TryParseBitcoinDescriptor_HardenedMarkersNormalized()
    {
        var withH = "wpkh([e17c2d80/84h/0h/0h]xpub6BemYiVNp19a19pfjF1QyNfD9vWnUYcZFgqo1m2cRP7GJJ7j9QZKuEGHnP775g4dFWFBm1h9jDGzqoK617XnyamAcLATGaAC68Cm5sgVS1V/0/*)#chk";
        Assert.True(DescriptorParser.TryParseBitcoinDescriptor(withH,
            out _, out _, out var derivationPath, out _, out _));
        Assert.Equal("84'/0'/0'", derivationPath);
    }

    [Fact]
    public void TryParseBitcoinDescriptor_TrailingGarbage_Rejected()
    {
        // Anchored regex (PR #11) rejects descriptors with content after the
        // optional checksum. Pre-PR-#11 would have silently accepted.
        // Use a non-alphanumeric extension so it can't be absorbed into the
        // checksum character class.
        var withGarbage = AquaBtc + "!!extra";
        Assert.False(DescriptorParser.TryParseBitcoinDescriptor(withGarbage,
            out _, out _, out _, out _, out var error));
        Assert.Contains("Invalid BTC descriptor", error);
    }

    [Fact]
    public void TryParseBitcoinDescriptor_Empty_ReturnsError()
    {
        Assert.False(DescriptorParser.TryParseBitcoinDescriptor("",
            out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseBitcoinDescriptor_Null_ReturnsError()
    {
        Assert.False(DescriptorParser.TryParseBitcoinDescriptor(null,
            out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    // ---- TryParseLiquidDescriptor ----

    [Fact]
    public void TryParseLiquidDescriptor_AquaWrapped_ParsesWithP2shSuffix()
    {
        Assert.True(DescriptorParser.TryParseLiquidDescriptor(AquaLbtcWrapped,
            out var blindingKey, out var suffix, out var fingerprint,
            out var derivationPath, out var xpub, out var error));
        Assert.Null(error);
        Assert.Equal("c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403", blindingKey);
        Assert.Equal("-[p2sh]", suffix);
        Assert.Equal("e17c2d80", fingerprint);
        Assert.Equal("49'/1776'/0'", derivationPath);
        Assert.StartsWith("xpub6BemYiVNp19a2", xpub);
    }

    [Fact]
    public void TryParseLiquidDescriptor_BullNative_ParsesWithEmptySuffix()
    {
        Assert.True(DescriptorParser.TryParseLiquidDescriptor(BullLbtcNative,
            out var blindingKey, out var suffix, out var fingerprint,
            out var derivationPath, out var xpub, out var error));
        Assert.Null(error);
        Assert.Equal("c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403", blindingKey);
        Assert.Equal("", suffix);
        Assert.Equal("e17c2d80", fingerprint);
        Assert.Equal("84'/1776'/0'", derivationPath);
        Assert.StartsWith("xpub6BemYiVNp19a2", xpub);
    }

    [Fact]
    public void TryParseLiquidDescriptor_WrappedNonWpkh_Rejected()
    {
        // PR #10 silent bonus fix: pre-PR, master would have accepted
        // elsh(pkh(...)) and treated it as P2SH-P2WPKH, producing wrong
        // addresses. The parser now validates the inner script type.
        var withPkh = AquaLbtcWrapped.Replace("elsh(wpkh(", "elsh(pkh(");
        Assert.False(DescriptorParser.TryParseLiquidDescriptor(withPkh,
            out _, out _, out _, out _, out _, out var error));
        Assert.Contains("Unsupported LBTC script type: elsh(pkh)", error);
    }

    [Fact]
    public void TryParseLiquidDescriptor_Malformed_ReturnsError()
    {
        Assert.False(DescriptorParser.TryParseLiquidDescriptor("not-a-descriptor",
            out _, out _, out _, out _, out _, out var error));
        Assert.Contains("Invalid LBTC descriptor", error);
    }

    [Fact]
    public void TryParseLiquidDescriptor_Empty_ReturnsError()
    {
        Assert.False(DescriptorParser.TryParseLiquidDescriptor("",
            out _, out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseLiquidDescriptor_Null_ReturnsError()
    {
        Assert.False(DescriptorParser.TryParseLiquidDescriptor(null,
            out _, out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseLiquidDescriptor_TrailingGarbage_Rejected()
    {
        var withGarbage = AquaLbtcWrapped + "!!extra";
        Assert.False(DescriptorParser.TryParseLiquidDescriptor(withGarbage,
            out _, out _, out _, out _, out _, out _));
    }
}
