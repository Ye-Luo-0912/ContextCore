using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Context Learning Loop 只读页面。</summary>
public static class ServiceLearningScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceLearningSnapshotAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderLearning(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, R refresh, S <recordId>, C <caseId>: ");
            var input = Console.ReadLine();
            var normalized = (input ?? string.Empty).Trim();

            if (normalized.StartsWith("s ", StringComparison.OrdinalIgnoreCase))
            {
                var recordId = normalized[2..].Trim();
                try
                {
                    var record = await service.GetServiceLearningRecordAsync(recordId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderLearning(new ServiceLearningSnapshot
                    {
                        CurrentTime = DateTimeOffset.Now,
                        BaseUrl = service.State.ServiceBaseUrl ?? string.Empty,
                        FeedbackSignals = [],
                        Records = [record],
                        Cases = [],
                        PositiveCount = record.Signal == ContextFeedbackSignal.Positive ? 1 : 0,
                        NegativeCount = record.Signal == ContextFeedbackSignal.Negative ? 1 : 0,
                        StaleCount = record.Signal == ContextFeedbackSignal.Stale ? 1 : 0,
                        FailureTypeSummary = new Dictionary<ContextFailureType, int>
                        {
                            [record.FailureType] = 1
                        }
                    }));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (normalized.StartsWith("c ", StringComparison.OrdinalIgnoreCase))
            {
                var caseId = normalized[2..].Trim();
                try
                {
                    var learningCase = await service.GetServiceLearningCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderLearning(new ServiceLearningSnapshot
                    {
                        CurrentTime = DateTimeOffset.Now,
                        BaseUrl = service.State.ServiceBaseUrl ?? string.Empty,
                        FeedbackSignals = [],
                        Records = [],
                        Cases = [learningCase],
                        PositiveCount = 0,
                        NegativeCount = 0,
                        StaleCount = 0,
                        FailureTypeSummary = new Dictionary<ContextFailureType, int>
                        {
                            [learningCase.FailureType] = 1
                        }
                    }));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            var action = ControlRoomInteraction.InterpretDetailInput(input);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }
        }
    }
}
