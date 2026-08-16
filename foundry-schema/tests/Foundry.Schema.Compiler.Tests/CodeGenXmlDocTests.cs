using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class CodeGenXmlDocTests
{
    [Fact]
    public void NullInput_ReturnsEmptyString()
    {
        var result = CodeGen.XmlDoc(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void EmptyStringInput_ReturnsEmptyString()
    {
        var result = CodeGen.XmlDoc(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhitespaceOnlyInput_ReturnsEmptyString()
    {
        var result = CodeGen.XmlDoc("   \t\n ");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SingleLinePlainText_WrappedInSummaryWithTripleSlashes()
    {
        var text = "This is a summary.";
        var result = CodeGen.XmlDoc(text);

        Assert.Contains("/// <summary>", result);
        Assert.Contains("This is a summary.", result);
        Assert.Contains("/// </summary>", result);
    }

    [Fact]
    public void MultiLineText_SplitsOnNewlinesCorrectly()
    {
        var text = "First line.\r\nSecond line.\nThird line.\rFourth line.";
        var result = CodeGen.XmlDoc(text);

        // Verify exact structure: each line on its own line with /// prefix and newline separator
        // Split on all newline types: \r\n becomes one split, \r and \n each become splits
        var lines = result.Split('\n');
        // After split: <summary>, 4 content, </summary>, plus one empty from trailing \n, plus potential extra = 7
        Assert.True(lines.Length >= 6, $"Expected at least 6 lines, got {lines.Length}");
        Assert.Equal("/// <summary>", lines[0]);
        Assert.Equal("/// First line.", lines[1]);
        Assert.Equal("/// Second line.", lines[2]);
        Assert.Equal("/// Third line.", lines[3]);
        Assert.Equal("/// Fourth line.", lines[4]);
        Assert.Equal("/// </summary>", lines[5]);
    }

    [Fact]
    public void AmpersandEscapedBeforeAngleBrackets()
    {
        var text = "A & B < C > D";
        var result = CodeGen.XmlDoc(text);

        // Ampersand should be escaped first to prevent double-escaping
        Assert.Contains("A &amp; B &lt; C &gt; D", result);
    }

    [Fact]
    public void AngleBracketsEscapedCorrectly()
    {
        var text = "x < y && z > w";
        var result = CodeGen.XmlDoc(text);

        // Verify proper escaping
        Assert.Contains("&lt;", result);
        Assert.Contains("&gt;", result);
        // Verify unescaped versions don't appear in their problematic context
        Assert.DoesNotContain("< y", result);
        Assert.DoesNotContain("> w", result);
    }

    [Fact]
    public void BlockCommentDelimitersEscapedCorrectly()
    {
        var text = "This contains /* not a comment */ stuff";
        var result = CodeGen.XmlDoc(text);

        // The asterisks are not special in XML, only & and angle brackets are escaped
        // So /* should appear as is, but < and > would be escaped if present
        Assert.Contains("/*", result);
        Assert.Contains("*/", result);
    }

    [Fact]
    public void ArrowAndCommentTerminatorEscaped()
    {
        var text = "Example: --> ends comment";
        var result = CodeGen.XmlDoc(text);

        // The > in --> should be escaped to &gt;
        Assert.Contains("--&gt;", result);
    }

    [Fact]
    public void EveryLineHasTripleSlashPrefix()
    {
        var text = "First line\nSecond line";
        var result = CodeGen.XmlDoc(text);

        // Verify exact structure: every non-empty output line starts with ///
        // Expected: <summary>, First line, Second line, </summary>, trailing empty
        var lines = result.Split('\n');
        Assert.Equal(5, lines.Length);
        // All non-empty lines must start with ///
        for (int i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                Assert.True(lines[i].StartsWith("///"), $"Line {i} '{lines[i]}' does not start with '///'");
            }
        }
    }

    [Fact]
    public void ResultEndsWithTrailingNewline()
    {
        var text = "Summary text.";
        var result = CodeGen.XmlDoc(text);

        Assert.EndsWith("\n", result);
    }

    [Fact]
    public void ControlCharactersStrippedExceptTab()
    {
        // Tab (9) is allowed, other C0 control chars (0-8, 10-12, 14-31) are stripped
        // Use (char)7 (bell) and (char)8 (backspace) instead of \r and \n which are newlines and get split
        var text = "Start" + (char)1 + (char)2 + "\t" + (char)7 + "End";
        var result = CodeGen.XmlDoc(text);

        // Tab should be preserved
        Assert.Contains("\t", result);
        // Content should be preserved (control chars 1,2,7 are stripped, so Start, tab, and End appear together)
        Assert.Contains("Start", result);
        Assert.Contains("End", result);
        // The result should have tab between Start and End with no control chars
        Assert.Contains("Start\tEnd", result);
    }

    [Fact]
    public void SummaryTagsProperlyFormatted()
    {
        var text = "Test content";
        var result = CodeGen.XmlDoc(text);

        // Ensure <summary> and </summary> tags are present with proper formatting
        Assert.Contains("/// <summary>", result);
        Assert.Contains("/// </summary>", result);
        // They should be on separate lines
        var lines = result.Split('\n');
        bool foundStart = false, foundEnd = false;
        foreach (var line in lines)
        {
            if (line.Contains("<summary>")) foundStart = true;
            if (line.Contains("</summary>")) foundEnd = true;
        }
        Assert.True(foundStart, "Opening <summary> tag not found");
        Assert.True(foundEnd, "Closing </summary> tag not found");
    }

    [Fact]
    public void IndentationAppliedToAllLines()
    {
        var text = "Line 1\nLine 2";
        var result = CodeGen.XmlDoc(text, indentSpaces: 4);

        var lines = result.Split('\n');
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Assert.True(line.StartsWith("    ///"), $"Line '{line}' does not start with 4 spaces + '///'");
            }
        }
    }

    [Fact]
    public void ComplexInputWithAllSpecialCharacters()
    {
        var text = "A & B < C > D";
        var result = CodeGen.XmlDoc(text);

        // Verify proper escaping order: & first, then < and >
        Assert.Contains("A &amp; B &lt; C &gt; D", result);
        // Verify unescaped versions don't appear adjacent
        Assert.DoesNotContain("& B <", result);
    }

    [Fact]
    public void LessThanGreaterThanAndAmpersandInSequence()
    {
        var text = "&<>";
        var result = CodeGen.XmlDoc(text);

        // All three should be escaped, & must be first to avoid double-escape
        Assert.Contains("&amp;&lt;&gt;", result);
    }

    [Fact]
    public void MultipleConsecutiveNewlines()
    {
        var text = "Line 1\n\n\nLine 2";
        var result = CodeGen.XmlDoc(text);

        // Blank lines in input should become empty /// lines in output
        // Input has: "Line 1", "", "", "Line 2" (4 logical lines)
        // Output should have: <summary>, Line 1, empty, empty, Line 2, </summary>, trailing
        var lines = result.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.Equal("/// <summary>", lines[0]);
        Assert.Equal("/// Line 1", lines[1]);
        Assert.Equal("/// ", lines[2]);  // First blank line becomes empty /// line
        Assert.Equal("/// ", lines[3]);  // Second blank line becomes empty /// line
        Assert.Equal("/// Line 2", lines[4]);
        Assert.Equal("/// </summary>", lines[5]);
    }
}
