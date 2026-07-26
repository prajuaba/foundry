using Foundry.Testing.Reports;
using Xunit;

namespace Foundry.Testing.Tests;

/// <summary>
/// What the generated test report is allowed to claim.
/// </summary>
/// <remarks>
/// A test report is read as evidence. If it asserts things it was never told — a protocol passing, a
/// coverage percentage, "Zero Breach" — then a failing run produces a document that looks like a
/// clean bill of health, and the report becomes actively worse than no report. This file exists to
/// keep every claim traceable to an input.
/// </remarks>
public class TestReportGeneratorTests
{
    // ---- claims must follow the numbers ----

    [Fact]
    public void AFailingRunIsNotReportedAsAllPassed()
    {
        // Both report formats hardcoded a seven-row "Protocol Coverage Matrix" with every row marked
        // PASSED, independent of the results. A run with 50 failures produced a report stating that
        // REST, GraphQL, Kafka, WebSockets, FileIO, Business Rules and Workflows had all passed.
        //
        // Asserted against the fabricated rows specifically. "PASSED" on its own is a legitimate
        // label for the passed-count metric, so a blanket search would fail on correct output.
        var html = TestReportGenerator.GenerateHtmlReport("My.Domain", totalTests: 100, passedTests: 50, failedTests: 50, durationSeconds: 1);
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", totalTests: 100, passedTests: 50, failedTests: 50, durationSeconds: 1);

        foreach (var report in new[] { html, markdown })
        {
            foreach (var protocol in new[]
                     {
                         "REST API", "GraphQL", "Kafka Outbox", "Real-Time WebSockets",
                         "FileIO Service", "Business Rules", "Workflow State Machine"
                     })
            {
                Assert.DoesNotContain(protocol, report);
            }
        }

        // And the overall verdict must not read as a pass.
        Assert.DoesNotContain("all 100 test(s) passed", markdown);
    }

    [Fact]
    public void AFailingRunSaysSoProminently()
    {
        var html = TestReportGenerator.GenerateHtmlReport("My.Domain", 100, 50, 50, 1);
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 100, 50, 50, 1);

        Assert.Contains("FAILED", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FAILED", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APassingRunIsReportedAsPassing()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 100, 100, 0, 1);

        Assert.Contains("PASSED", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoUnsubstantiatedCoverageClaimsAreMade()
    {
        // "100% Endpoint Coverage", "Zero Breach", "Security Enforced" and "KRaft Verified" were
        // fixed strings in the template. Nothing measured any of them.
        var html = TestReportGenerator.GenerateHtmlReport("My.Domain", 10, 10, 0, 1);

        foreach (var claim in new[] { "100% Endpoint Coverage", "Zero Breach", "Security Enforced", "KRaft Verified" })
        {
            Assert.DoesNotContain(claim, html);
        }
    }

    [Fact]
    public void ARunWithNoTestsDoesNotClaimAPerfectPassRate()
    {
        // 0 tests produced "Pass Rate: 100.0%", which is the same falsehood in numeric form: a suite
        // that never ran reported as fully passing.
        var html = TestReportGenerator.GenerateHtmlReport("My.Domain", 0, 0, 0, 0);
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 0, 0, 0, 0);

        Assert.DoesNotContain("100.0%", html);
        Assert.DoesNotContain("100.0%", markdown);
    }

    [Fact]
    public void ARunWithNoTestsSaysThatExplicitly()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 0, 0, 0, 0);

        Assert.Contains("no tests", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNumbersGivenAreReported()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 42, 40, 2, 3.5);

        Assert.Contains("42", markdown);
        Assert.Contains("40", markdown);
        Assert.Contains("2", markdown);
        Assert.Contains("My.Domain", markdown);
    }

    [Fact]
    public void ThePassRateIsComputedFromTheCounts()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 200, 150, 50, 1);
        Assert.Contains("75.0%", markdown);
    }

    // ---- per-protocol results, when actually supplied ----

    [Fact]
    public void SuppliedProtocolResultsAreRendered()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport(
            "My.Domain", 10, 9, 1, 1,
            [new ProtocolResult("REST API", Passed: true, "CRUD"), new ProtocolResult("Kafka Outbox", Passed: false, "Outbox")]);

        Assert.Contains("REST API", markdown);
        Assert.Contains("Kafka Outbox", markdown);
    }

    [Fact]
    public void AFailingProtocolIsNotShownAsPassing()
    {
        var markdown = TestReportGenerator.GenerateMarkdownReport(
            "My.Domain", 10, 9, 1, 1,
            [new ProtocolResult("Kafka Outbox", Passed: false, "Outbox")]);

        var kafkaRow = markdown.Split('\n').First(l => l.Contains("Kafka Outbox"));
        Assert.DoesNotContain("PASSED", kafkaRow);
    }

    [Fact]
    public void WithNoProtocolResultsSuppliedNothingIsInvented()
    {
        // The method only receives totals, so it cannot know per-protocol outcomes. Saying nothing is
        // the only honest option.
        var markdown = TestReportGenerator.GenerateMarkdownReport("My.Domain", 10, 10, 0, 1);

        Assert.DoesNotContain("REST API", markdown);
        Assert.DoesNotContain("GraphQL", markdown);
    }

    // ---- output hygiene ----

    [Fact]
    public void TheNamespaceIsHtmlEscaped()
    {
        // The namespace comes from the schema, which may be AI-authored. Validation restricts it, but
        // this method is public and callable with anything, and the report is opened in a browser.
        var html = TestReportGenerator.GenerateHtmlReport(
            "<script>alert('x')</script>", 10, 10, 0, 1);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void TheHtmlReportIsWellFormedEnoughToOpen()
    {
        var html = TestReportGenerator.GenerateHtmlReport("My.Domain", 10, 10, 0, 1);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
    }
}
