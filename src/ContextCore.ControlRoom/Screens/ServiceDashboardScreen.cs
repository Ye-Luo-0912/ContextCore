using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的最小运行时仪表盘，默认不自动执行 deep probe。</summary>
public static class ServiceDashboardScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        var includeDeep = false;

        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceDashboardSnapshotAsync(
                    includeDeep: includeDeep,
                    refreshDeep: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceDashboardRenderer.RenderToString(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceDashboardRenderer.RenderErrorToString(service.State.ServiceBaseUrl ?? string.Empty, ex));
            }

            Console.Write("> ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "d", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    includeDeep = true;
                    var snapshot = await service.GetServiceDashboardSnapshotAsync(
                        includeDeep: true,
                        refreshDeep: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceDashboardRenderer.RenderToString(snapshot));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceDashboardRenderer.RenderErrorToString(service.State.ServiceBaseUrl ?? string.Empty, ex));
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "i", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceIngestScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "g", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceQueryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "v", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePackageScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "j", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceJobsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "m", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceModelScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "u", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceAdminRuntimeScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "k", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceConstraintsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "c", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceConstraintGapsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "e", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceCandidateConstraintsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "l", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceRelationsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "o", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePolicyScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "t", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceShortTermMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePromotionCandidatesScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "h", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceLearningScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "32", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePolicyFeedbackScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "33", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceLearningFeaturesScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "x", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePlanningSnapshotScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "f", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServicePlanningProposalScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "34", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceRankerShadowDebugScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "35", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceCandidateMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "36", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceStableMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }

            if (action.Kind == ControlRoomActionKind.Value
                && string.Equals(action.Value?.Trim(), "37", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ServiceVectorIndexScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false);
                if (result == ControlRoomActionKind.Quit)
                {
                    return result;
                }

                continue;
            }
        }
    }
}


