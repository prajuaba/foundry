using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Configuration for reaching a local Ollama instance.
    /// </summary>
    public sealed record AiGenerationOptions
    {
        /// <summary>Default host when nothing is configured.</summary>
        public const string DefaultHost = "http://localhost:11434";

        /// <summary>Default model when nothing is configured.</summary>
        public const string DefaultModel = "qwen3-coder:30b";

        /// <summary>Ollama base URL, without a trailing slash.</summary>
        public string Host { get; init; } = DefaultHost;

        /// <summary>Model tag to invoke.</summary>
        public string Model { get; init; } = DefaultModel;

        /// <summary>
        /// Maximum number of repair attempts after the first generation.
        /// </summary>
        public int MaxRepairAttempts { get; init; } = 3;

        /// <summary>
        /// How many extra attempts may be spent improving a document that is already valid but
        /// carries a repairable warning.
        /// </summary>
        /// <remarks>
        /// One by default. A valid document in hand is worth more than a speculative better one,
        /// and each additional round costs a full generation; a single retry captures most of the
        /// benefit without letting the loop chase warnings indefinitely.
        /// </remarks>
        public int MaxSoftRepairAttempts { get; init; } = 1;

        /// <summary>Per-request timeout.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Sampling temperature. Low by default: this is a structured-extraction task, not a
        /// creative one, and determinism makes the repair loop converge.
        /// </summary>
        public double Temperature { get; init; } = 0.1;

        /// <summary>
        /// Resolves options from explicit values, then environment, then defaults.
        /// </summary>
        /// <remarks>
        /// Replaces the previous behaviour of hardcoding a specific developer's machine name
        /// (<c>edgexpert-c1ad.local</c>) as the default host in both the backend and the Studio
        /// store, which made the feature fail for everyone else.
        /// </remarks>
        public static AiGenerationOptions Resolve(string? host = null, string? model = null)
        {
            var resolvedHost = FirstNonEmpty(
                host,
                Environment.GetEnvironmentVariable("FOUNDRY_OLLAMA_HOST"),
                Environment.GetEnvironmentVariable("OLLAMA_HOST"),
                DefaultHost)!;

            if (!resolvedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !resolvedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                resolvedHost = "http://" + resolvedHost;
            }

            return new AiGenerationOptions
            {
                Host = resolvedHost.TrimEnd('/'),
                Model = FirstNonEmpty(
                    model,
                    Environment.GetEnvironmentVariable("FOUNDRY_OLLAMA_MODEL"),
                    DefaultModel)!
            };
        }

        private static string? FirstNonEmpty(params string?[] candidates)
            => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
    }

    /// <summary>
    /// One generation attempt and its validation outcome.
    /// </summary>
    /// <param name="Attempt">1-based attempt number.</param>
    /// <param name="RawResponse">Exactly what the model returned.</param>
    /// <param name="Diagnostics">Validation result for that response.</param>
    public sealed record AiAttempt(int Attempt, string RawResponse, IReadOnlyList<Diagnostic> Diagnostics);

    /// <summary>
    /// Result of an AI-assisted IR generation run.
    /// </summary>
    public sealed record AiGenerationResult
    {
        /// <summary>The best document produced, or null if none parsed.</summary>
        public SchemaModel? Schema { get; init; }

        /// <summary>True when the returned document validated with no errors.</summary>
        public bool Success { get; init; }

        /// <summary>Diagnostics still outstanding on the returned document.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();

        /// <summary>Every attempt made, for transparency and debugging.</summary>
        public IReadOnlyList<AiAttempt> Attempts { get; init; } = Array.Empty<AiAttempt>();

        /// <summary>Populated when the run failed for a reason other than validation.</summary>
        public string? Error { get; init; }

        /// <summary>
        /// True when generation was grammar-constrained to the IR schema. False means the run fell
        /// back to plain JSON mode and relied entirely on validate-and-repair.
        /// </summary>
        public bool GrammarConstrained { get; init; } = true;

        /// <summary>Why the grammar was abandoned, when <see cref="GrammarConstrained"/> is false.</summary>
        public string? GrammarFallbackReason { get; init; }
    }

    /// <summary>
    /// A non-success HTTP response from Ollama, carrying enough detail to classify the failure.
    /// </summary>
    internal sealed class OllamaHttpException : Exception
    {
        /// <summary>HTTP status code returned.</summary>
        public int StatusCode { get; }

        /// <summary>Response body, truncated.</summary>
        public string Body { get; }

        /// <summary>Initialises the exception.</summary>
        public OllamaHttpException(int statusCode, string body)
            : base($"HTTP {statusCode}: {body}")
        {
            StatusCode = statusCode;
            Body = body;
        }

        /// <summary>
        /// True when Ollama refused the request because it could not compile the supplied JSON
        /// Schema into a sampling grammar.
        /// </summary>
        /// <remarks>
        /// Matched on the message text because Ollama does not expose a distinct error code for
        /// this. Kept deliberately narrow: only a 400 mentioning grammar or sampler initialisation
        /// triggers the fallback, so genuine bad requests still surface as errors.
        /// </remarks>
        public bool IsGrammarRejection =>
            StatusCode == 400
            && (Body.Contains("grammar", StringComparison.OrdinalIgnoreCase)
                || Body.Contains("sampler", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Turns a natural-language instruction into a validated Foundry IR document using a local model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two mechanisms carry the reliability here, and neither is prompt engineering:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Grammar-constrained decoding.</b> The IR JSON Schema is passed in Ollama's
    /// <c>format</c> field, so the sampler cannot emit a field name outside the schema, an
    /// unsupported attribute, or an identifier containing punctuation. The previous integration
    /// sent <c>format: "json"</c>, which only guarantees *some* valid JSON — it let the model
    /// invent field names the compiler silently discarded.
    /// </description></item>
    /// <item><description>
    /// <b>A closed repair loop.</b> Structural validity is not semantic validity: a grammar
    /// cannot know that a transition targets an undeclared state. So the result is validated and
    /// any diagnostics are fed back verbatim, since each carries a hint phrased as an edit to the
    /// IR document.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class AiSchemaGenerator
    {
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _client;
        private readonly AiGenerationOptions _options;

        // Set once if the server rejects the IR grammar, so the remaining attempts in a run do not
        // each pay a failed round-trip re-discovering the same thing.
        private bool _grammarDisabled;
        private string? _grammarFallbackReason;

        /// <summary>Initialises the generator.</summary>
        public AiSchemaGenerator(HttpClient client, AiGenerationOptions options)
        {
            _client = client;
            _options = options;
            if (_client.Timeout != options.Timeout)
                _client.Timeout = options.Timeout;
        }

        /// <summary>
        /// Generates an IR document from an instruction, repairing validation failures in a loop.
        /// </summary>
        /// <param name="instruction">What the user asked for, in natural language.</param>
        /// <param name="currentSchema">Existing document to modify, or null to start fresh.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<AiGenerationResult> GenerateAsync(
            string instruction,
            SchemaModel? currentSchema = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(instruction))
                return new AiGenerationResult { Error = "Instruction is required." };

            var irSchema = ParseIrSchema();
            // Pass the instruction so only the construct sections it needs are included; a prompt
            // carrying every section measurably degraded this model's attention.
            var systemPrompt = AiSpecBundle.BuildSystemPrompt(currentSchema, instruction);
            var attempts = new List<AiAttempt>();

            var prompt = $"{systemPrompt}\n\nINSTRUCTION\n{instruction}\n";
            SchemaModel? best = null;
            IReadOnlyList<Diagnostic> bestDiagnostics = Array.Empty<Diagnostic>();

            // A document that already validated. Soft repairs may try to improve on it, but if a
            // later attempt comes back worse this is what gets returned.
            SchemaModel? firstValid = null;
            IReadOnlyList<Diagnostic> firstValidDiagnostics = Array.Empty<Diagnostic>();
            var softRepairsUsed = 0;

            // One initial generation plus up to MaxRepairAttempts corrections.
            var totalAttempts = Math.Max(1, _options.MaxRepairAttempts + 1);

            for (var attempt = 1; attempt <= totalAttempts; attempt++)
            {
                string raw;
                try
                {
                    raw = await GenerateOnceAsync(prompt, irSchema, ct);
                }
                catch (Exception ex)
                {
                    return new AiGenerationResult
                    {
                        Schema = best,
                        Diagnostics = bestDiagnostics,
                        Attempts = attempts,
                        GrammarConstrained = !_grammarDisabled,
                        GrammarFallbackReason = _grammarFallbackReason,
                        Error = $"Ollama request failed on attempt {attempt}: {ex.Message}"
                    };
                }

                SchemaModel? candidate;
                DiagnosticBag bag;

                try
                {
                    candidate = JsonSerializer.Deserialize<SchemaModel>(raw, ReadOptions);
                    bag = SchemaValidator.Validate(candidate);
                }
                catch (JsonException ex)
                {
                    // Should be unreachable under a grammar, but a model can still be truncated
                    // by a token limit mid-document.
                    candidate = null;
                    bag = new DiagnosticBag();
                    bag.Error(
                        DiagnosticCatalog.MissingNamespace,
                        $"Model returned unparseable JSON: {ex.Message}",
                        "",
                        "Return a single complete JSON object and nothing else.");
                }

                attempts.Add(new AiAttempt(attempt, raw, bag.Items));

                if (!bag.HasErrors)
                {
                    var repairable = bag.Items
                        .Where(d => d.Severity == DiagnosticSeverity.Warning
                                    && DiagnosticCatalog.RepairableWarnings.Contains(d.Code))
                        .ToList();

                    // Hold on to the first valid document. Everything below may spend an attempt
                    // trying to improve on it, and must never end up returning something worse.
                    firstValid ??= candidate;
                    firstValidDiagnostics = firstValid == candidate ? bag.Items : firstValidDiagnostics;

                    var canSoftRepair = repairable.Count > 0
                                        && softRepairsUsed < _options.MaxSoftRepairAttempts
                                        && attempt < totalAttempts;

                    if (!canSoftRepair)
                    {
                        return new AiGenerationResult
                        {
                            Schema = candidate,
                            Success = true,
                            Diagnostics = bag.Items,
                            Attempts = attempts,
                            GrammarConstrained = !_grammarDisabled,
                            GrammarFallbackReason = _grammarFallbackReason
                        };
                    }

                    // Valid but probably not what was meant. Ask once more, feeding back only the
                    // warnings that state a mechanical fix.
                    softRepairsUsed++;
                    prompt = BuildRepairPrompt(systemPrompt, instruction, raw, bag);
                    continue;
                }

                // Keep the most nearly-correct candidate so a failed run still returns something useful.
                if (candidate is not null && (best is null || bag.ErrorCount < bestDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)))
                {
                    best = candidate;
                    bestDiagnostics = bag.Items;
                }

                if (attempt == totalAttempts) break;

                prompt = BuildRepairPrompt(systemPrompt, instruction, raw, bag);
            }

            // A soft repair that failed to improve things still leaves the earlier valid
            // document, which is a better answer than reporting failure.
            if (firstValid is not null)
            {
                return new AiGenerationResult
                {
                    Schema = firstValid,
                    Success = true,
                    Diagnostics = firstValidDiagnostics,
                    Attempts = attempts,
                    GrammarConstrained = !_grammarDisabled,
                    GrammarFallbackReason = _grammarFallbackReason
                };
            }

            return new AiGenerationResult
            {
                Schema = best,
                Success = false,
                Diagnostics = bestDiagnostics,
                Attempts = attempts,
                GrammarConstrained = !_grammarDisabled,
                GrammarFallbackReason = _grammarFallbackReason,
                Error = $"Could not produce a valid IR document in {totalAttempts} attempt(s)."
            };
        }

        /// <summary>
        /// Performs one generation, degrading from grammar-constrained to plain JSON mode if the
        /// server cannot compile the schema.
        /// </summary>
        /// <remarks>
        /// Grammar support varies by Ollama version and by the schema's own constructs, and a
        /// rejection fails the whole request rather than any single field. Hard-failing there would
        /// take the entire AI feature offline for a schema detail most users cannot diagnose, so the
        /// generator falls back to <c>format: "json"</c> and leans on validate-and-repair instead.
        /// The result records that this happened rather than hiding it: constrained decoding is a
        /// correctness guarantee, and its absence is worth surfacing.
        /// </remarks>
        private async Task<string> GenerateOnceAsync(string prompt, JsonElement irSchema, CancellationToken ct)
        {
            if (!_grammarDisabled)
            {
                try
                {
                    return await CallOllamaAsync(prompt, irSchema, ct);
                }
                catch (OllamaHttpException ex) when (ex.IsGrammarRejection)
                {
                    _grammarDisabled = true;
                    _grammarFallbackReason =
                        $"Ollama could not compile the IR schema into a sampling grammar ({Truncate(ex.Body, 200)}). "
                        + "Falling back to plain JSON mode; validation and repair still apply.";
                }
            }

            return await CallOllamaAsync(prompt, "json", ct);
        }

        private static string BuildRepairPrompt(string systemPrompt, string instruction, string previous, DiagnosticBag bag)
        {
            var errors = bag.Items
                .Where(d => d.Severity == DiagnosticSeverity.Error
                            || (d.Severity == DiagnosticSeverity.Warning
                                && DiagnosticCatalog.RepairableWarnings.Contains(d.Code)))
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(systemPrompt);
            sb.AppendLine();
            sb.AppendLine("INSTRUCTION");
            sb.AppendLine(instruction);
            sb.AppendLine();
            sb.AppendLine("Your previous attempt was rejected by the Foundry validator.");
            sb.AppendLine();
            sb.AppendLine("PREVIOUS ATTEMPT");
            sb.AppendLine(previous);
            sb.AppendLine();
            sb.AppendLine("ERRORS TO FIX");
            foreach (var d in errors)
            {
                sb.AppendLine($"- {d.Code} at {(string.IsNullOrEmpty(d.Path) ? "(document)" : d.Path)}: {d.Message}");
                if (!string.IsNullOrEmpty(d.Hint))
                    sb.AppendLine($"  fix: {d.Hint}");
            }
            sb.AppendLine();
            sb.AppendLine("Return the corrected complete IR document. Fix only these errors; keep everything else.");
            return sb.ToString();
        }

        /// <param name="format">
        /// Either the IR JSON Schema (constrains decoding to a sampling grammar) or the string
        /// <c>"json"</c> (loose JSON mode).
        /// </param>
        private async Task<string> CallOllamaAsync(string prompt, object format, CancellationToken ct)
        {
            var request = new
            {
                model = _options.Model,
                prompt,
                stream = false,
                format,
                options = new
                {
                    temperature = _options.Temperature
                }
            };

            var response = await _client.PostAsJsonAsync($"{_options.Host}/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new OllamaHttpException((int)response.StatusCode, Truncate(body, 400));
            }

            var payload = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Response))
                throw new InvalidOperationException("Ollama returned an empty response.");

            return payload.Response.Trim();
        }

        /// <summary>
        /// Verifies the configured Ollama host is reachable and the model is present.
        /// </summary>
        public async Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                var tags = await _client.GetFromJsonAsync<OllamaTagsPayload>($"{_options.Host}/api/tags", ct);
                var names = tags?.Models?.Select(m => m.Name).ToList() ?? new List<string>();

                if (names.Count == 0)
                    return (false, $"{_options.Host} responded but has no models installed.");

                if (!names.Contains(_options.Model, StringComparer.OrdinalIgnoreCase))
                    return (false, $"Model '{_options.Model}' not found on {_options.Host}. Available: {string.Join(", ", names)}");

                return (true, $"{_options.Host} ready, model '{_options.Model}' available.");
            }
            catch (Exception ex)
            {
                return (false, $"Cannot reach {_options.Host}: {ex.Message}");
            }
        }

        private static JsonElement ParseIrSchema()
        {
            using var doc = JsonDocument.Parse(IrSchemaGenerator.Generate());
            return doc.RootElement.Clone();
        }

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max) + "…";

        private sealed record OllamaGenerateResponse
        {
            [JsonPropertyName("response")]
            public string Response { get; init; } = string.Empty;
        }

        private sealed record OllamaTagsPayload
        {
            [JsonPropertyName("models")]
            public List<OllamaTagEntry>? Models { get; init; }
        }

        private sealed record OllamaTagEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;
        }
    }
}
