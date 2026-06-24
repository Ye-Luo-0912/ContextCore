namespace ContextCore.Abstractions.Models;

/// <summary>预定义的上下文关系类型常量，用于统一标识条目间关系的语义。</summary>
public static class ContextRelationTypes
{
    /// <summary>派生自另一条目。</summary>
    public const string DerivedFrom = "derived_from";

    /// <summary>对另一条目的摘要。</summary>
    public const string Summarizes = "summarizes";

    /// <summary>由模型或自动流程生成。</summary>
    public const string GeneratedBy = "generated_by";

    /// <summary>引用另一条目。</summary>
    public const string References = "references";

    /// <summary>被包含在某个上下文包中。</summary>
    public const string IncludedInPackage = "included_in_package";

    /// <summary>与另一条目相关联（通用关系）。</summary>
    public const string RelatedTo = "related_to";

    /// <summary>依赖于另一条目。</summary>
    public const string DependsOn = "depends_on";

    /// <summary>与另一条目存在矛盾。</summary>
    public const string Contradicts = "contradicts";

    /// <summary>与另一条目内容重复。</summary>
    public const string Duplicates = "duplicates";

    /// <summary>替换（取代）另一条目。</summary>
    public const string Replaces = "replaces";

    /// <summary>被另一条目替代。</summary>
    public const string SupersededBy = "superseded_by";

    /// <summary>指向替代当前条目的新条目。</summary>
    public const string ReplacedBy = "replaced_by";

    /// <summary>适用于另一条目。</summary>
    public const string AppliesTo = "applies_to";

    /// <summary>由候选项审核接受后提升而来。</summary>
    public const string PromotedFrom = "promoted_from";

    /// <summary>作为另一条目的证据来源。</summary>
    public const string EvidenceFor = "evidence_for";
}
