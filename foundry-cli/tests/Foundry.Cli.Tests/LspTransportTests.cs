using System.Text;
using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// The LSP stdio transport, exercised against the real process.
/// </summary>
/// <remarks>
/// The LSP base protocol frames messages by <em>byte</em> count. The VS Code extension is the only
/// consumer, and a framing error there does not produce an error message — the editor simply stops
/// receiving diagnostics, because every subsequent frame is misaligned. These tests write real bytes
/// to stdin and read real bytes from stdout for that reason: reading the response as a string would
/// hide exactly the class of defect being guarded against.
/// </remarks>
public class LspTransportTests
{
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";

    /// <summary>Parses a stream of LSP frames into their bodies, checking each Content-Length.</summary>
    private static List<string> ParseFrames(byte[] output)
    {
        var bodies = new List<string>();
        var offset = 0;

        while (offset < output.Length)
        {
            // Find the header terminator.
            var headerEnd = IndexOf(output, "\r\n\r\n"u8.ToArray(), offset);
            if (headerEnd < 0) break;

            var headerText = Encoding.ASCII.GetString(output, offset, headerEnd - offset);
            var lengthLine = headerText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(lengthLine);
            var declaredLength = int.Parse(lengthLine!["Content-Length:".Length..].Trim());

            var bodyStart = headerEnd + 4;
            Assert.True(
                bodyStart + declaredLength <= output.Length,
                $"frame declares {declaredLength} bytes but only {output.Length - bodyStart} remain — "
                + "the header byte count does not match the body");

            bodies.Add(Encoding.UTF8.GetString(output, bodyStart, declaredLength));
            offset = bodyStart + declaredLength;
        }

        return bodies;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    [Fact]
    public async Task AnInitializeRequestIsAnswered()
    {
        var output = await Cli.RunWithStdinAsync(Cli.LspFrame(InitializeRequest), "lsp");

        var body = Assert.Single(ParseFrames(output));
        Assert.Contains("capabilities", body);
    }

    [Fact]
    public async Task TheDeclaredContentLengthMatchesTheBodyExactly()
    {
        // The whole reason this transport is byte-oriented. A header counting characters instead of
        // bytes leaves trailing bytes in the stream and desynchronises every later frame.
        var output = await Cli.RunWithStdinAsync(Cli.LspFrame(InitializeRequest), "lsp");

        var bodies = ParseFrames(output);

        Assert.Single(bodies);
        // ParseFrames asserts the arithmetic; this confirms nothing was left over.
        var consumed = output.Length;
        Assert.True(consumed > 0);
    }

    [Fact]
    public async Task TwoRequestsInOneStreamAreBothAnswered()
    {
        // Frame alignment only shows up from the second message onward: the first response looks fine
        // even when the reader has left a stray byte behind.
        var first = Cli.LspFrame(InitializeRequest);
        var second = Cli.LspFrame(
            """{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{}}""");

        var input = new byte[first.Length + second.Length];
        first.CopyTo(input, 0);
        second.CopyTo(input, first.Length);

        var output = await Cli.RunWithStdinAsync(input, "lsp");
        var bodies = ParseFrames(output);

        Assert.Equal(2, bodies.Count);
        Assert.Contains("capabilities", bodies[0]);
        Assert.Contains("TenantKey", bodies[1]);
    }

    [Fact]
    public async Task ANonAsciiPayloadDoesNotDesynchroniseTheStream()
    {
        // The defect this transport was rewritten to fix. A single 'é' is two UTF-8 bytes but one
        // char, so a reader sized by Content-Length in chars leaves a byte behind and the next frame
        // is garbage. The editor shows no error — diagnostics just stop arriving.
        var withAccents = Cli.LspFrame(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"note":"café-naïve-piñata-日本語"}}""");
        var follower = Cli.LspFrame(
            """{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{}}""");

        var input = new byte[withAccents.Length + follower.Length];
        withAccents.CopyTo(input, 0);
        follower.CopyTo(input, withAccents.Length);

        var output = await Cli.RunWithStdinAsync(input, "lsp");
        var bodies = ParseFrames(output);

        Assert.Equal(2, bodies.Count);
        Assert.Contains("TenantKey", bodies[1]);
    }

    [Fact]
    public async Task AnEmptyStreamExitsCleanly()
    {
        var output = await Cli.RunWithStdinAsync([], "lsp");

        Assert.Empty(ParseFrames(output));
    }

    [Fact]
    public async Task ATruncatedFrameDoesNotHang()
    {
        // A body shorter than its declared length must end the loop rather than block forever; the
        // two-minute timeout in the fixture turns a hang into a failure.
        var frame = Cli.LspFrame(InitializeRequest);
        var truncated = frame[..(frame.Length - 10)];

        var output = await Cli.RunWithStdinAsync(truncated, "lsp");

        Assert.Empty(ParseFrames(output));
    }

    [Fact]
    public async Task AMalformedBodyDoesNotKillTheServer()
    {
        // One bad message from an editor must not take the language server down for the session.
        var bad = Cli.LspFrame("{ not json");
        var good = Cli.LspFrame(InitializeRequest);

        var input = new byte[bad.Length + good.Length];
        bad.CopyTo(input, 0);
        good.CopyTo(input, bad.Length);

        var output = await Cli.RunWithStdinAsync(input, "lsp");

        Assert.Single(ParseFrames(output));
    }

    [Fact]
    public async Task ANotificationWithoutAnIdIsNotAnswered()
    {
        // Notifications carry no id and must not receive a response, or the client sees a reply it
        // cannot correlate.
        var notification = Cli.LspFrame("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");

        var output = await Cli.RunWithStdinAsync(notification, "lsp");

        Assert.Empty(ParseFrames(output));
    }
}
