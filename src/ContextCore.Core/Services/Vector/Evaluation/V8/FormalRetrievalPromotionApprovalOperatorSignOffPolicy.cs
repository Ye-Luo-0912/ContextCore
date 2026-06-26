namespace ContextCore.Core.Services;

/// <summary>V8.15 operator sign-off 状态。</summary>
public static class OperatorSignOffStatuses
{
    /// <summary>上游 ApplicationReady + RollbackReady 双就绪未满足 — sign-off 不评估。</summary>
    public const string OperatorSignOffNotApplicable = nameof(OperatorSignOffNotApplicable);

    /// <summary>双就绪满足，但至少一个凭据结构要素缺失。</summary>
    public const string OperatorSignOffInsufficient = nameof(OperatorSignOffInsufficient);

    /// <summary>双就绪满足 + 凭据结构齐备；sign-off 仅作"已记录"。Recorded ≠ Crossed — 仍未跨过应用边界。</summary>
    public const string OperatorSignOffRecorded = nameof(OperatorSignOffRecorded);
}

/// <summary>V8.15 sign-off 凭据要素 — 每一项都是 artifact / structural 检查，不是 interactive review。</summary>
public static class OperatorSignOffElements
{
    /// <summary>操作者身份字段非空且符合命名约定（who is signing）。</summary>
    public const string OperatorIdentityPresent = nameof(OperatorIdentityPresent);

    /// <summary>该身份对该 capability + scope 拥有权限的证明（authority proof artifact）。</summary>
    public const string OperatorAuthorityProofPresent = nameof(OperatorAuthorityProofPresent);

    /// <summary>意图字段明确肯定（"apply this grant in this scope"），不是默认或空。</summary>
    public const string SignOffIntentAffirmative = nameof(SignOffIntentAffirmative);

    /// <summary>sign-off 时间戳在有效窗口内（不太久远 / 不是未来）。</summary>
    public const string SignOffTimestampWithinValidityWindow = nameof(SignOffTimestampWithinValidityWindow);

    /// <summary>密码学签名 / 校验码已附上且结构有效。</summary>
    public const string SignOffCryptographicSealValid = nameof(SignOffCryptographicSealValid);

    public static readonly IReadOnlyList<string> AllInOrder = new[]
    {
        OperatorIdentityPresent,
        OperatorAuthorityProofPresent,
        SignOffIntentAffirmative,
        SignOffTimestampWithinValidityWindow,
        SignOffCryptographicSealValid
    };
}

/// <summary>V8.15 sign-off 凭据结构状态 — boolean fields，纯结构验证。</summary>
public sealed class OperatorSignOffCredentials
{
    public bool OperatorIdentityPresent { get; init; }
    public bool OperatorAuthorityProofPresent { get; init; }
    public bool SignOffIntentAffirmative { get; init; }
    public bool SignOffTimestampWithinValidityWindow { get; init; }
    public bool SignOffCryptographicSealValid { get; init; }
}

/// <summary>V8.15 sign-off decision。判定"凭据是否完整足以被记录为 sign-off"；Crossed 永远 false。</summary>
public sealed class OperatorSignOffDecision
{
    public string Status { get; init; } = OperatorSignOffStatuses.OperatorSignOffNotApplicable;
    public string InputApplicationStatus { get; init; } = string.Empty;
    public string InputRollbackStatus { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public IReadOnlyList<string> CredentialElementsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CredentialElementsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>关键不变量 — 即便 Recorded，应用边界也未被跨过。</summary>
    public bool Crossed { get; init; }

    /// <summary>从 V8.13 carry — Recorded 不等于应用已发生。</summary>
    public bool ApplicationApplied { get; init; }

    /// <summary>从 V8.14 carry — Recorded 也不等于回滚路径被激活。</summary>
    public bool RollbackActivated { get; init; }
}

/// <summary>
/// V8.15 explicit operator sign-off policy。给定 V8.13 + V8.14 双就绪决策 + 凭据结构状态，判定 sign-off 凭据是否齐备。
/// 纯函数；Crossed / ApplicationApplied / RollbackActivated 永远 false。
/// </summary>
public static class FormalRetrievalPromotionApprovalOperatorSignOffPolicy
{
    public static OperatorSignOffDecision Evaluate(
        GrantApplicationDecision applicationDecision,
        RollbackReadinessDecision rollbackDecision,
        OperatorSignOffCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(applicationDecision);
        ArgumentNullException.ThrowIfNull(rollbackDecision);
        ArgumentNullException.ThrowIfNull(credentials);

        var applicationReady = string.Equals(applicationDecision.Status, GrantApplicationStatuses.GrantApplicationReady, StringComparison.Ordinal);
        var rollbackReady = string.Equals(rollbackDecision.Status, RollbackReadinessStatuses.RollbackReady, StringComparison.Ordinal);

        // 必须 application Ready 且 rollback Ready；任何一边不就绪 → sign-off 不评估。
        if (!applicationReady || !rollbackReady)
        {
            return new OperatorSignOffDecision
            {
                Status = OperatorSignOffStatuses.OperatorSignOffNotApplicable,
                InputApplicationStatus = applicationDecision.Status,
                InputRollbackStatus = rollbackDecision.Status,
                RequestedCapability = applicationDecision.RequestedCapability,
                RequestedScope = applicationDecision.RequestedScope,
                CredentialElementsMet = Array.Empty<string>(),
                CredentialElementsMissing = Array.Empty<string>(),
                Reasoning = $"application status '{applicationDecision.Status}' + rollback status '{rollbackDecision.Status}' do not jointly satisfy ApplicationReady && RollbackReady; sign-off not evaluated.",
                Crossed = false,
                ApplicationApplied = false,
                RollbackActivated = false
            };
        }

        var met = new List<string>();
        var missing = new List<string>();
        AddByFlag(credentials.OperatorIdentityPresent, OperatorSignOffElements.OperatorIdentityPresent, met, missing);
        AddByFlag(credentials.OperatorAuthorityProofPresent, OperatorSignOffElements.OperatorAuthorityProofPresent, met, missing);
        AddByFlag(credentials.SignOffIntentAffirmative, OperatorSignOffElements.SignOffIntentAffirmative, met, missing);
        AddByFlag(credentials.SignOffTimestampWithinValidityWindow, OperatorSignOffElements.SignOffTimestampWithinValidityWindow, met, missing);
        AddByFlag(credentials.SignOffCryptographicSealValid, OperatorSignOffElements.SignOffCryptographicSealValid, met, missing);

        if (missing.Count > 0)
        {
            return new OperatorSignOffDecision
            {
                Status = OperatorSignOffStatuses.OperatorSignOffInsufficient,
                InputApplicationStatus = applicationDecision.Status,
                InputRollbackStatus = rollbackDecision.Status,
                RequestedCapability = applicationDecision.RequestedCapability,
                RequestedScope = applicationDecision.RequestedScope,
                CredentialElementsMet = met,
                CredentialElementsMissing = missing,
                Reasoning = $"{missing.Count} sign-off credential element(s) missing; sign-off insufficient.",
                Crossed = false,
                ApplicationApplied = false,
                RollbackActivated = false
            };
        }

        return new OperatorSignOffDecision
        {
            Status = OperatorSignOffStatuses.OperatorSignOffRecorded,
            InputApplicationStatus = applicationDecision.Status,
            InputRollbackStatus = rollbackDecision.Status,
            RequestedCapability = applicationDecision.RequestedCapability,
            RequestedScope = applicationDecision.RequestedScope,
            CredentialElementsMet = met,
            CredentialElementsMissing = Array.Empty<string>(),
            Reasoning = "all sign-off credential elements present; sign-off Recorded. Note: Recorded != Crossed. The application boundary remains uncrossed; this matrix does not execute application, does not activate rollback path.",
            Crossed = false,  // 关键不变量 — Recorded 不是 Crossed。
            ApplicationApplied = false,
            RollbackActivated = false
        };
    }

    private static void AddByFlag(bool flag, string name, List<string> met, List<string> missing)
    {
        if (flag)
        {
            met.Add(name);
        }
        else
        {
            missing.Add(name);
        }
    }
}
