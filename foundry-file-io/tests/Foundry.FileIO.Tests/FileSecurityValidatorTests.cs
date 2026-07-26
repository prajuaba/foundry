using System.Text;
using Foundry.FileIO;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// Filename sanitisation and signature verification.
/// </summary>
/// <remarks>
/// This type's whole purpose is to be the boundary between an uploaded file and the filesystem, so
/// anything it lets through is by definition unchecked. Its output is fed to <c>Path.Combine</c>
/// against an upload directory, which is what makes the traversal cases below consequential rather
/// than cosmetic.
/// </remarks>
public class FileSecurityValidatorTests
{
    private static readonly FileSecurityValidator Validator = new();

    // ---- filename sanitisation ----

    [Theory]
    [InlineData("report.csv", "report.csv")]
    [InlineData("annual report 2026.xlsx", "annual report 2026.xlsx")]
    public void OrdinaryNamesSurviveUnchanged(string input, string expected)
    {
        Assert.Equal(expected, Validator.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("subdir/report.csv")]
    public void DirectoryComponentsAreStripped(string input)
    {
        var result = Validator.SanitizeFileName(input);

        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("..  ")]
    public void ATraversalComponentIsNeverReturned(string input)
    {
        // Path.GetFileName("..") returns "..", and '.' is not an invalid filename character, so a
        // name consisting only of dots passed straight through a method documented as eliminating
        // path traversal. Path.Combine(uploadDir, "..") resolves to the parent directory.
        var result = Validator.SanitizeFileName(input);

        Assert.NotEqual("..", result.Trim());
        Assert.NotEqual(".", result.Trim());
        Assert.False(
            result.Trim().All(c => c == '.'),
            $"sanitised name '{result}' consists only of dots");
    }

    [Fact]
    public void ADotOnlyNameBecomesAGeneratedName()
    {
        var result = Validator.SanitizeFileName("..");

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.DoesNotContain("..", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyNameBecomesAGeneratedName(string? input)
    {
        var result = Validator.SanitizeFileName(input!);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void TheExtensionIsPreserved()
    {
        // Downstream code keys content handling off the extension, so losing it changes behaviour.
        Assert.EndsWith(".csv", Validator.SanitizeFileName("../data.csv"));
    }

    [Fact]
    public void AnOverlongNameIsTruncated()
    {
        // Most filesystems cap a single name at 255 bytes. An untruncated name fails at write time
        // with an IOException from deep inside the storage layer rather than being rejected here.
        var result = Validator.SanitizeFileName(new string('a', 500) + ".csv");

        Assert.True(result.Length <= 255, $"sanitised name was {result.Length} characters");
        Assert.EndsWith(".csv", result);
    }

    // ---- signature verification ----

    private static MemoryStream StreamOf(params byte[] bytes) => new(bytes);

    [Fact]
    public void AMatchingSignatureIsAccepted()
    {
        Assert.True(Validator.VerifySignature("logo.png", StreamOf(0x89, 0x50, 0x4E, 0x47, 0x0D)));
    }

    [Fact]
    public void AMismatchedSignatureIsRejected()
    {
        // The case this exists for: an executable renamed to .png.
        Assert.False(Validator.VerifySignature("logo.png", StreamOf(0x4D, 0x5A, 0x90, 0x00)));
    }

    [Fact]
    public void AnEmptyFileIsRejectedForACheckedType()
    {
        Assert.False(Validator.VerifySignature("logo.png", new MemoryStream()));
    }

    [Fact]
    public void ATruncatedHeaderIsRejected()
    {
        Assert.False(Validator.VerifySignature("logo.png", StreamOf(0x89, 0x50)));
    }

    [Fact]
    public void AnUncheckedExtensionIsAccepted()
    {
        // Documented behaviour: only known signatures are verified, so CSV and TXT pass. Asserted so
        // it is a deliberate contract rather than an accident -- a caller must not read this as
        // "the file is safe", only as "its signature does not contradict its extension".
        Assert.True(Validator.VerifySignature("data.csv", StreamOf(0x61, 0x62, 0x63)));
        Assert.True(Validator.VerifySignature("notes.txt", StreamOf(0x61)));
    }

    [Fact]
    public void TheExtensionCheckIsCaseInsensitive()
    {
        Assert.True(Validator.VerifySignature("LOGO.PNG", StreamOf(0x89, 0x50, 0x4E, 0x47)));
        Assert.False(Validator.VerifySignature("LOGO.PNG", StreamOf(0x00, 0x01, 0x02, 0x03)));
    }

    [Fact]
    public void TheStreamPositionIsRestored()
    {
        // The caller goes on to parse the same stream. Leaving the position past the header makes the
        // parser read from the wrong offset and mis-parse silently.
        var stream = StreamOf(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A);
        stream.Position = 2;

        Validator.VerifySignature("logo.png", stream);

        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void ANullStreamIsRejectedLoudly()
    {
        Assert.Throws<ArgumentNullException>(() => Validator.VerifySignature("logo.png", null!));
    }

    [Fact]
    public void AnEmptyFileNameIsRejected()
    {
        Assert.False(Validator.VerifySignature("", StreamOf(0x89)));
    }

    [Fact]
    public void ZipBasedOfficeFormatsShareTheirSignature()
    {
        var pkHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        Assert.True(Validator.VerifySignature("book.xlsx", StreamOf(pkHeader)));
        Assert.True(Validator.VerifySignature("archive.zip", StreamOf(pkHeader)));
    }
}
