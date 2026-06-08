using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>托管后台服务，持续轮询 ContextCore 作业队列并分发给对应处理器。</summary>
public sealed class ContextJobWorker : BackgroundService
{
	private readonly IServiceProvider _services;
	private readonly IOptions<JobWorkerOptions> _options;
	private readonly ILogger<ContextJobWorker> _logger;

	public ContextJobWorker(
		IServiceProvider services,
		IOptions<JobWorkerOptions> options,
		ILogger<ContextJobWorker> logger)
	{
		_services = services;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!_options.Value.Enabled)
		{
			_logger.LogInformation("Context job worker is disabled.");
			return;
		}

		var delay = TimeSpan.FromMilliseconds(Math.Max(100, _options.Value.PollIntervalMilliseconds));
		var concurrency = Math.Max(1, _options.Value.Concurrency);
		// SemaphoreSlim 控制最大并发槽位，PostgreSQL 队列使用 SELECT FOR UPDATE SKIP LOCKED
		// 确保多个并发槽位（或多个 worker 实例）不会重复消费同一作业。
		using var semaphore = new SemaphoreSlim(concurrency, concurrency);
		_logger.LogInformation(
			"Context job worker started. PollInterval={PollInterval}ms, Concurrency={Concurrency}.",
			delay.TotalMilliseconds, concurrency);

		while (!stoppingToken.IsCancellationRequested)
		{
			// 等待空闲槽位；若所有槽位占满则休眠 poll interval 再检查。
			if (!await semaphore.WaitAsync(delay, stoppingToken).ConfigureAwait(false))
			{
				continue;
			}

			// 每轮创建 scope，确保 scoped 存储或处理器生命周期正确。
			var scope = _services.CreateScope();
			var queue = scope.ServiceProvider.GetRequiredService<IContextJobQueue>();
			var job = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);

			if (job is null)
			{
				semaphore.Release();
				scope.Dispose();
				// 队列为空时短暂休眠，避免空转占满 CPU。
				await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
				continue;
			}

			// 异步执行作业，不阻塞轮询循环，允许同时处理多个作业。
			_ = Task.Run(async () =>
			{
				try
				{
					var dispatcher = scope.ServiceProvider.GetRequiredService<IContextJobDispatcher>();
					var eventSink = scope.ServiceProvider.GetRequiredService<IContextEventSink>();
					await dispatcher.DispatchAsync(job, stoppingToken).ConfigureAwait(false);
					await queue.AckAsync(job.JobId, stoppingToken).ConfigureAwait(false);
					await EmitAsync(eventSink, job, ContextEventLevel.Information, $"Job {job.JobId} succeeded.", stoppingToken).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
				{
					_logger.LogError(ex, "Context job {JobId} failed.", job.JobId);
					// 处理器抛出异常时 NackAsync，队列根据 retry_count 决定重试或终态。
					// 各 store 的写入操作均幂等（ON CONFLICT），保证重试不会产生脏数据。
					try
					{
						var queue2 = scope.ServiceProvider.GetRequiredService<IContextJobQueue>();
						var eventSink2 = scope.ServiceProvider.GetRequiredService<IContextEventSink>();
						await queue2.NackAsync(job.JobId, ex.Message, CancellationToken.None).ConfigureAwait(false);
						await EmitAsync(eventSink2, job, ContextEventLevel.Error, ex.Message, CancellationToken.None).ConfigureAwait(false);
					}
					catch (Exception nackEx)
					{
						_logger.LogError(nackEx, "Failed to nack job {JobId}.", job.JobId);
					}
				}
				finally
				{
					semaphore.Release();
					scope.Dispose();
				}
			}, stoppingToken);
		}
	}

	private static Task EmitAsync(
		IContextEventSink eventSink,
		ContextJob job,
		ContextEventLevel level,
		string message,
		CancellationToken cancellationToken)
	{
		return eventSink.EmitAsync(new ContextOperationEvent
		{
			EventId = Guid.NewGuid().ToString("N"),
			OperationId = job.JobId,
			OperationName = $"job.{job.Kind.ToString().ToLowerInvariant()}",
			WorkspaceId = job.WorkspaceId,
			CollectionId = job.CollectionId,
			Level = level,
			Message = message,
			Metadata = new Dictionary<string, string>
			{
				["jobKind"] = job.Kind.ToString(),
				["retryCount"] = job.RetryCount.ToString()
			},
			CreatedAt = DateTimeOffset.UtcNow
		}, cancellationToken);
	}
}
