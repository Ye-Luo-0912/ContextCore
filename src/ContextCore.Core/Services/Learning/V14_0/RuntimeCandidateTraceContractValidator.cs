using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V14_0;

public sealed class RuntimeCandidateTraceContractValidator
{
    private static readonly string[] CriticalFields = { "operationId", "candidateId", "sourceType", "authority", "retrievalChannel", "traceSource" };
    private static readonly string[] OptionalFields = { "sourceId", "strategyType", "deterministicScore", "strategyScore", "finalScore", "selectedByScoring", "includedInPackage", "droppedReason", "tokenCost", "section", "recordedAt" };

    public int MissingCriticalFieldCount { get; private set; }
    public int MissingOptionalFieldCount { get; private set; }
    public IReadOnlyList<RuntimeCandidateTraceMissingFieldReport> Reports { get; private set; } = Array.Empty<RuntimeCandidateTraceMissingFieldReport>();

    public void Validate(IReadOnlyList<string> jsonLines)
    {
        var reports = new List<RuntimeCandidateTraceMissingFieldReport>();
        int crit = 0, opt = 0;
        foreach (var line in jsonLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var d = JsonDocument.Parse(line).RootElement;
                var missingCritical = new List<string>();
                var missingOptional = new List<string>();
                string rowId = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(rowId)) rowId = d.TryGetProperty("operationId", out var o) ? o.GetString() ?? "" : "";

                foreach (var f in CriticalFields)
                {
                    if (!d.TryGetProperty(f, out _)) missingCritical.Add(f);
                    else if (f == "operationId" && (d.TryGetProperty("operationId", out var ov) && string.IsNullOrWhiteSpace(ov.GetString()))) missingCritical.Add("operationId(empty)");
                    else if (f == "candidateId" && (d.TryGetProperty("candidateId", out var cv) && string.IsNullOrWhiteSpace(cv.GetString()))) missingCritical.Add("candidateId(empty)");
                }
                foreach (var f in OptionalFields)
                    if (!d.TryGetProperty(f, out _)) missingOptional.Add(f);

                if (missingCritical.Count > 0 || missingOptional.Count > 0)
                {
                    crit += missingCritical.Count;
                    opt += missingOptional.Count;
                    reports.Add(new RuntimeCandidateTraceMissingFieldReport
                    {
                        RowIdentifier = rowId,
                        MissingCriticalFields = missingCritical,
                        MissingOptionalFields = missingOptional
                    });
                }
            }
            catch { crit++; reports.Add(new RuntimeCandidateTraceMissingFieldReport { RowIdentifier = "parse_error", MissingCriticalFields = new[] { "json_parse" } }); }
        }
        MissingCriticalFieldCount = crit;
        MissingOptionalFieldCount = opt;
        Reports = reports;
    }
}
