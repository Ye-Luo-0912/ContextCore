using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>Runs multiple attention profiles in shadow mode against the same eligible candidate pool.</summary>
internal sealed class AttentionProfileExperimentRunner
{
    private readonly IContextLearningStore? _learningStore;
    private readonly IReadOnlyList<ContextAttentionProfile> _profiles;

    public AttentionProfileExperimentRunner(
        IEnumerable<ContextAttentionProfile>? profiles = null,
        IContextLearningStore? learningStore = null)
    {
        _profiles = (profiles ?? ContextAttentionProfile.CreateShadowExperimentProfiles()).ToArray();
        _learningStore = learningStore;
    }

    public async Task<AttentionProfileExperimentReport> RunAsync(
        string operationId,
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        RetrievalPackingResult currentPackingResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(currentPackingResult);

        var results = new List<AttentionProfileExperimentResult>(_profiles.Count);
        foreach (var profile in _profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scorer = new RuleBasedContextAttentionScorer(profile, _learningStore);
            var scores = await scorer.ScoreAsync(request, rankedCandidates, cancellationToken).ConfigureAwait(false);
            var report = AttentionShadowReportBuilder.Build(
                operationId,
                request,
                rankedCandidates,
                currentPackingResult,
                scores);

            results.Add(new AttentionProfileExperimentResult
            {
                ProfileId = profile.ProfileId,
                PolicyVersion = profile.PolicyVersion,
                AttentionScores = scores,
                ShadowReport = report
            });
        }

        return new AttentionProfileExperimentReport
        {
            OperationId = operationId,
            Profiles = results
        };
    }
}
