using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderPolicy(ServicePolicySnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Policy");
        builder.AppendLine($"PersistedPolicies : {snapshot.Policies.Count}");
        builder.AppendLine($"DefaultPolicy     : {snapshot.DefaultPolicy.Name}");
        builder.AppendLine($"TokenBudget       : {snapshot.DefaultPolicy.TokenBudget}");
        builder.AppendLine($"SectionPriorities : {(snapshot.DefaultPolicy.SectionPriorities.Count == 0 ? "(default)" : string.Join(',', snapshot.DefaultPolicy.SectionPriorities.Select(p => $"{p.Key}={p.Value}")))}");
        builder.AppendLine("LifecyclePolicy");
        foreach (var note in snapshot.LifecycleNotes)
        {
            builder.AppendLine($"- {note}");
        }
        builder.AppendLine("ProviderCapabilities");
        foreach (var capability in snapshot.ProviderCapabilities)
        {
            builder.AppendLine($"- {capability.Name} [{capability.State}] active={(capability.Active ? "yes" : "no")}");
        }
        return builder.ToString();
    }
}
