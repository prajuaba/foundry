using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Foundry.Testing.Reports;

/// <summary>
/// The outcome of one protocol's or subsystem's test suite.
/// </summary>
/// <param name="Name">Protocol or subsystem name, e.g. "REST API".</param>
/// <param name="Passed">Whether its suite passed.</param>
/// <param name="Details">What the suite covered.</param>
public sealed record ProtocolResult(string Name, bool Passed, string Details);

/// <summary>
/// Renders test-run reports in HTML and Markdown.
/// </summary>
/// <remarks>
/// <para>
/// Every claim in the output is derived from an argument. Both formats previously embedded a fixed
/// seven-row "Protocol Coverage Matrix" in which every row read <c>PASSED</c>, alongside strings such
/// as "100% Endpoint Coverage", "Zero Breach" and "KRaft Verified" — none of which was measured, and
/// none of which changed when tests failed. A run with fifty failures produced a document stating
/// that every protocol had passed.
/// </para>
/// <para>
/// A report is read as evidence, so fabricated claims are worse than a missing report. Per-protocol
/// status is now rendered only when it is supplied; with nothing supplied, nothing is said.
/// </para>
/// </remarks>
public static class TestReportGenerator
{
    /// <summary>Renders an HTML report.</summary>
    public static string GenerateHtmlReport(
        string namespaceName,
        int totalTests,
        int passedTests,
        int failedTests,
        double durationSeconds,
        IReadOnlyList<ProtocolResult>? protocols = null)
    {
        // Escaped: the namespace originates in a schema, which may be AI- or user-authored, and this
        // output is opened in a browser.
        var safeNamespace = WebUtility.HtmlEncode(namespaceName ?? string.Empty);
        var overall = OverallStatus(totalTests, failedTests);

        var protocolRows = protocols is { Count: > 0 }
            ? string.Join("\n", protocols.Select(p =>
                $@"                <tr><td>{WebUtility.HtmlEncode(p.Name)}</td>"
                + $@"<td><span class=""{(p.Passed ? "pass" : "fail")}"">{(p.Passed ? "✔ PASSED" : "✘ FAILED")}</span></td>"
                + $@"<td>{WebUtility.HtmlEncode(p.Details)}</td></tr>"))
            : @"                <tr><td colspan=""3"" style=""color: #94a3b8;"">No per-protocol results were supplied for this run.</td></tr>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <title>Foundry Test Suite Report - {safeNamespace}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #f8fafc; padding: 40px; }}
        .card {{ background: #1e293b; border: 1px solid #334155; border-radius: 12px; padding: 24px; margin-bottom: 24px; }}
        .metric-grid {{ display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin-top: 20px; }}
        .metric {{ background: #0f172a; padding: 16px; border-radius: 8px; text-align: center; border: 1px solid #334155; }}
        .metric-val {{ font-size: 28px; font-weight: 800; margin-top: 4px; }}
        .pass {{ color: #4ade80; }}
        .fail {{ color: #f87171; }}
        .info {{ color: #38bdf8; }}
        table {{ width: 100%; border-collapse: collapse; margin-top: 16px; }}
        th, td {{ padding: 12px 16px; text-align: left; border-bottom: 1px solid #334155; font-size: 14px; }}
        th {{ background: #0f172a; color: #94a3b8; text-transform: uppercase; font-size: 12px; }}
    </style>
</head>
<body>
    <div class=""card"">
        <h1 style=""margin: 0; font-size: 24px; color: #38bdf8;"">⚡ Foundry Autonomous Test Execution Report</h1>
        <p style=""color: #94a3b8; margin: 4px 0 0 0;"">Target Namespace: <strong>{safeNamespace}</strong> | Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | Duration: {durationSeconds:F2}s</p>
        <p style=""margin: 12px 0 0 0;"" class=""{(overall.IsGood ? "pass" : "fail")}""><strong>{overall.Label}</strong></p>

        <div class=""metric-grid"">
            <div class=""metric""><span style=""color: #94a3b8; font-size: 12px;"">TOTAL TESTS</span><div class=""metric-val info"">{totalTests}</div></div>
            <div class=""metric""><span style=""color: #94a3b8; font-size: 12px;"">PASSED</span><div class=""metric-val pass"">{passedTests}</div></div>
            <div class=""metric""><span style=""color: #94a3b8; font-size: 12px;"">FAILED</span><div class=""metric-val fail"">{failedTests}</div></div>
            <div class=""metric""><span style=""color: #94a3b8; font-size: 12px;"">PASS RATE</span><div class=""metric-val {(overall.IsGood ? "pass" : "fail")}"">{PassRateText(totalTests, passedTests)}</div></div>
        </div>
    </div>

    <div class=""card"">
        <h3 style=""margin: 0 0 16px 0;"">Protocol Coverage Summary</h3>
        <table>
            <thead>
                <tr>
                    <th>Protocol / Subsystem</th>
                    <th>Status</th>
                    <th>Coverage Details</th>
                </tr>
            </thead>
            <tbody>
{protocolRows}
            </tbody>
        </table>
    </div>
</body>
</html>";
    }

    /// <summary>Renders a Markdown report.</summary>
    public static string GenerateMarkdownReport(
        string namespaceName,
        int totalTests,
        int passedTests,
        int failedTests,
        double durationSeconds,
        IReadOnlyList<ProtocolResult>? protocols = null)
    {
        var overall = OverallStatus(totalTests, failedTests);
        var sb = new StringBuilder();

        sb.AppendLine("# ⚡ Foundry Autonomous Test Execution Report");
        sb.AppendLine($"**Namespace**: `{namespaceName}`  ");
        sb.AppendLine($"**Generated**: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Duration**: {durationSeconds:F2} seconds  ");
        sb.AppendLine($"**Result**: {overall.Label}\n");

        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| :--- | :---: |");
        sb.AppendLine($"| **Total Tests** | `{totalTests}` |");
        sb.AppendLine($"| **Passed** | `{passedTests}` |");
        sb.AppendLine($"| **Failed** | `{failedTests}` |");
        sb.AppendLine($"| **Pass Rate** | `{PassRateText(totalTests, passedTests)}` |\n");

        sb.AppendLine("### Protocol Coverage Matrix");

        if (protocols is { Count: > 0 })
        {
            sb.AppendLine("| Protocol | Status | Coverage |");
            sb.AppendLine("| :--- | :---: | :--- |");
            foreach (var protocol in protocols)
            {
                sb.AppendLine($"| {protocol.Name} | {(protocol.Passed ? "✅ PASSED" : "❌ FAILED")} | {protocol.Details} |");
            }
        }
        else
        {
            sb.AppendLine("No per-protocol results were supplied for this run.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Derives the overall verdict from the only two facts available.
    /// </summary>
    /// <remarks>
    /// A run with no tests is not a pass. It used to render as a 100% pass rate, which is the same
    /// falsehood as the hardcoded matrix expressed numerically.
    /// </remarks>
    private static (string Label, bool IsGood) OverallStatus(int totalTests, int failedTests)
    {
        if (totalTests <= 0) return ("INCONCLUSIVE — no tests were executed", false);
        return failedTests > 0
            ? ($"FAILED — {failedTests} of {totalTests} test(s) did not pass", false)
            : ($"PASSED — all {totalTests} test(s) passed", true);
    }

    private static string PassRateText(int totalTests, int passedTests)
        => totalTests > 0 ? $"{passedTests * 100.0 / totalTests:F1}%" : "n/a";
}
