using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public static class FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation
{
    private static readonly string[] EvidenceRequiredFields =
    [
        "ApprovalEvidenceId",
        "ApprovedBy",
        "ApprovalId",
        "ApprovalScopes",
        "ApprovalSource",
        "ApprovalTimestamp",
        "SourcePromotionPlanGateOperationId",
        "SourceReadinessGateOperationId",
        "SourceCloseoutGateOperationId",
        "OperatorStatement",
        "EvidenceCreatedAt",
        "ApprovalEvidenceSourceKind",
        "ApprovalEvidenceProvenanceId",
        "ApprovalEvidenceProvidedBy",
        "ApprovalEvidenceProvidedAt",
        "ApprovalEvidenceTrustMode",
        "ApprovalEvidenceIsExternal",
        "ApprovalEvidenceChecksum",
        "SourceApprovalRequestId",
        "BoundPendingApprovalGateOperationId"
    ];

    private static readonly string[] RegistryRequiredFields =
    [
        "RegistryId",
        "RegistryCreatedAt",
        "AllowedSourceKinds",
        "TrustedProvenanceRecords"
    ];

    private static readonly string[] RegistryRecordRequiredFields =
    [
        "ApprovalEvidenceProvenanceId",
        "ApprovalEvidenceSourceKind",
        "ApprovalEvidenceProvidedBy",
        "ApprovalEvidenceChecksum",
        "SourceApprovalRequestId",
        "BoundPendingApprovalGateOperationId",
        "AllowedScopes",
        "TrustMode",
        "ValidUntil"
    ];

    public static FormalRetrievalPromotionExternalApprovalCandidateValidationResult ValidateEvidenceJson(string jsonContent)
    {
        var missing = new List<string>();
        var invalid = new List<string>();
        FormalRetrievalPromotionApprovalEvidence? evidence = null;
        var parsed = false;

        try
        {
            evidence = JsonSerializer.Deserialize<FormalRetrievalPromotionApprovalEvidence>(jsonContent);
            parsed = evidence is not null;
        }
        catch
        {
            missing.Add("<evidence-parse-error>");
        }

        var candidateValid = evidence is not null
            && !string.IsNullOrWhiteSpace(evidence.ApprovalEvidenceId)
            && !string.IsNullOrWhiteSpace(evidence.ApprovedBy);
        var schemaValid = ValidateCandidateFields(jsonContent, EvidenceRequiredFields, missing, invalid);

        return new FormalRetrievalPromotionExternalApprovalCandidateValidationResult
        {
            Parsed = parsed,
            CandidateValid = candidateValid,
            SchemaValid = schemaValid,
            MissingFields = missing.Distinct(StringComparer.Ordinal).ToArray(),
            InvalidFields = invalid.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public static FormalRetrievalPromotionExternalApprovalCandidateValidationResult ValidateTrustRegistryJson(string jsonContent)
    {
        var missing = new List<string>();
        var invalid = new List<string>();
        FormalRetrievalPromotionApprovalTrustRegistry? registry = null;
        var parsed = false;

        try
        {
            registry = JsonSerializer.Deserialize<FormalRetrievalPromotionApprovalTrustRegistry>(jsonContent);
            parsed = registry is not null;
        }
        catch
        {
            missing.Add("<registry-parse-error>");
        }

        var candidateValid = registry is not null
            && !string.IsNullOrWhiteSpace(registry.RegistryId)
            && registry.TrustedProvenanceRecords.Count > 0;
        var schemaValid = ValidateCandidateFields(jsonContent, RegistryRequiredFields, missing, invalid);

        if (schemaValid && registry is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var records = doc.RootElement.GetProperty("TrustedProvenanceRecords");
                for (var i = 0; i < records.GetArrayLength(); i++)
                {
                    var rec = records[i];
                    foreach (var field in RegistryRecordRequiredFields)
                    {
                        if (!rec.TryGetProperty(field, out var prop))
                        {
                            missing.Add($"TrustedProvenanceRecords[{i}].{field}");
                            schemaValid = false;
                            continue;
                        }

                        var isInvalid = prop.ValueKind switch
                        {
                            JsonValueKind.String => string.IsNullOrWhiteSpace(prop.GetString()),
                            JsonValueKind.Array => prop.GetArrayLength() == 0,
                            _ => false
                        };

                        if (isInvalid)
                        {
                            invalid.Add($"TrustedProvenanceRecords[{i}].{field}");
                            schemaValid = false;
                        }

                        if (field == "ValidUntil"
                            && prop.TryGetDateTimeOffset(out var validUntil)
                            && (validUntil == default || validUntil.Year < 2000))
                        {
                            invalid.Add($"TrustedProvenanceRecords[{i}].{field}");
                            schemaValid = false;
                        }
                    }
                }
            }
            catch
            {
                missing.Add("<registry-record-parse-error>");
                schemaValid = false;
            }
        }

        return new FormalRetrievalPromotionExternalApprovalCandidateValidationResult
        {
            Parsed = parsed,
            CandidateValid = candidateValid,
            SchemaValid = schemaValid,
            MissingFields = missing.Distinct(StringComparer.Ordinal).ToArray(),
            InvalidFields = invalid.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public static bool ValidateCandidateFields(
        string jsonContent,
        IReadOnlyList<string> fieldNames,
        List<string> missing,
        List<string> invalid)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            missing.Add("<empty-json>");
            return false;
        }

        var allValid = true;
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;
            foreach (var field in fieldNames)
            {
                if (!root.TryGetProperty(field, out var prop))
                {
                    missing.Add(field);
                    allValid = false;
                    continue;
                }

                var invalidValue = prop.ValueKind switch
                {
                    JsonValueKind.String => string.IsNullOrWhiteSpace(prop.GetString()),
                    JsonValueKind.Array => prop.GetArrayLength() == 0,
                    JsonValueKind.Number => prop.GetDecimal() == 0 && prop.GetRawText() == "0",
                    JsonValueKind.True or JsonValueKind.False => !prop.GetBoolean(),
                    _ => false
                };

                if (invalidValue)
                {
                    invalid.Add(field);
                    allValid = false;
                }

                if (field is "ApprovalTimestamp" or "EvidenceCreatedAt" or "ApprovalEvidenceProvidedAt" or "RegistryCreatedAt"
                    && prop.TryGetDateTimeOffset(out var dt)
                    && (dt == default || dt.Year < 2000))
                {
                    invalid.Add(field);
                    allValid = false;
                }
            }
        }
        catch
        {
            missing.Add("<parse-error>");
            return false;
        }

        return allValid;
    }
}

public sealed class FormalRetrievalPromotionExternalApprovalCandidateValidationResult
{
    public bool Parsed { get; init; }
    public bool CandidateValid { get; init; }
    public bool SchemaValid { get; init; }
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvalidFields { get; init; } = Array.Empty<string>();
}
