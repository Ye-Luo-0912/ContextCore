using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>托管后台服务，持续轮询 ContextCore 作业队列并分发给对应处理器。</summary>
/// <remarks>
/// P0-4：当队列实现 <see cref="ILeasedJobQueue"/> 时（如 Postgres），worker 切换到租约路径：
/// 使用 <see cref="ILeasedJobQueue.AcquireLeaseAsync"/> 获取带租约的作业，
/// 处理过程中周期性调用 <see cref="ILeasedJobQueue.RenewHeartbeatAsync"/> 续约；
/// 进程崩溃后过期租约被其他 worker 抢占恢复，避免 Running 任务永久滞留。
/// 续约失败（返回 false）时 worker 中止处理且不 Ack/Nack——保留 state='Running'，
/// 让其他 worker 的 AcquireLeaseAsync 通过 (state='Running' AND lease_expires_at &lt;= now) 抢占。
/// 队列未实现 <see cref="ILeasedJobQueue"/> 时（如 InMemory/File）回退到 <see cref="IContextJobQueue.DequeueAsync"/> 路径。
/// </remarks>
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
		// P0-4：租约配置。仅当队列实现 ILeasedJobQueue 时生效；否则走 Dequeue 路径。
		var leaseDuration = _options.Value.LeaseDuration;
		var heartbeatInterval = _options.Value.HeartbeatInterval;
		var owner = GenerateOwnerId();
		// SemaphoreSlim 控制最大并发槽位，PostgreSQL 队列使用 SELECT FOR UPDATE SKIP LOCKED
		// 确保多个并发槽位（或多个 worker 实例）不会重复消费同一作业。
		using var semaphore = new SemaphoreSlim(concurrency, concurrency);
		_logger.LogInformation(
			"Context job worker started. PollInterval={PollInterval}ms, Concurrency={Concurrency}, Owner={Owner}, LeaseDuration={LeaseDuration}, HeartbeatInterval={HeartbeatInterval}.",
			delay.TotalMilliseconds, concurrency, owner, leaseDuration, heartbeatInterval);

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
			// P0-4：检测队列是否支持租约语义。Postgres 实现 ILeasedJobQueue；InMemory/File 不实现。
			var leasedQueue = queue as ILeasedJobQueue;

			ContextJob? job;
			if (leasedQueue is not null)
			{
				job = await leasedQueue.AcquireLeaseAsync(owner, leaseDuration, cancellationToken: stoppingToken)
					.ConfigureAwait(false);
			}
			else
			{
				job = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
			}

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
				// P0-4：租约路径下启动 heartbeat 任务，周期性续约。
				// leaseCts 链接到 stoppingToken——host 关闭时也会取消租约。
				// heartbeat 续约失败时取消 leaseCts，让 DispatchAsync 抛出 OperationCanceledException，
				// 主任务据此识别租约丢失并跳过 Ack/Nack。
				CancellationTokenSource? leaseCts = null;
				Task? heartbeatTask = null;

				if (leasedQueue is not null)
				{
					leaseCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
					heartbeatTask = RunHeartbeatAsync(
						leasedQueue, job.JobId, owner, leaseDuration, heartbeatInterval, leaseCts);
				}

				var effectiveToken = leaseCts?.Token ?? stoppingToken;

				try
				{
					var dispatcher = scope.ServiceProvider.GetRequiredService<IContextJobDispatcher>();
					var eventSink = scope.ServiceProvider.GetRequiredService<IContextEventSink>();
					await dispatcher.DispatchAsync(job, effectiveToken).ConfigureAwait(false);
					await queue.AckAsync(job.JobId, stoppingToken).ConfigureAwait(false);
					// R12.4A #9: Event Sink fail-open——作业已成功并 Ack，sink 发射失败不得触发 Nack/error 路径。
					// 之前 EmitAsync 抛出会落入外层 catch，导致对已 Ack 的作业执行 NackAsync（CAS 下为 no-op）
					// 并发射误导性的 Error 事件。现在单独捕获并降级为 Warning 日志。
					try
					{
						await EmitAsync(eventSink, job, ContextEventLevel.Information, $"Job {job.JobId} succeeded.", stoppingToken).ConfigureAwait(false);
					}
					catch (Exception emitEx)
					{
						_logger.LogWarning(emitEx, "Event sink failed to emit success event for job {JobId}. Job already acked, ignoring.", job.JobId);
					}
				}
				catch (OperationCanceledException) when (leaseCts is not null && leaseCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
				{
					// P0-4：租约丢失——RenewHeartbeatAsync 返回 false，heartbeat 任务已取消 leaseCts。
					// 不 Ack/Nack：保留 state='Running'，lease_expires_at 已过期，
					// 其他 worker 的 AcquireLeaseAsync 会通过 (state='Running' AND lease_expires_at <= now) 抢占恢复。
					// 注意：仅在 stoppingToken 未取消时判定为租约丢失——host 关闭导致的取消不算租约丢失。
					_logger.LogWarning("Job {JobId} aborted due to lease loss. Another worker may re-acquire.", job.JobId);
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
					// P0-4：停止 heartbeat 任务并释放 leaseCts。
					// leaseCts.Cancel() 让 heartbeat 的 Task.Delay 抛出 OperationCanceledException 而退出。
					if (leaseCts is not null)
					{
						leaseCts.Cancel();
						if (heartbeatTask is not null)
						{
							try { await heartbeatTask.ConfigureAwait(false); }
							catch { /* heartbeat 异常已在内部记录，此处忽略 */ }
						}
						leaseCts.Dispose();
					}
					semaphore.Release();
					scope.Dispose();
				}
			}, stoppingToken);
		}
	}

	/// <summary>
	/// P0-4：周期性续约租约。续约失败（返回 false）时取消 leaseCts，让 DispatchAsync 抛出 OperationCanceledException。
	/// 续约异常视为瞬时错误，等待下一次续约——若 lease 真的过期，下一次 RenewHeartbeatAsync 仍会返回 false。
	/// </summary>
	private async Task RunHeartbeatAsync(
		ILeasedJobQueue queue,
		string jobId,
		string owner,
		TimeSpan leaseDuration,
		TimeSpan heartbeatInterval,
		CancellationTokenSource leaseCts)
	{
		while (!leaseCts.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(heartbeatInterval, leaseCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return; // leaseCts 已取消（主任务完成或 host 关闭）——退出
			}

			if (leaseCts.IsCancellationRequested) return;

			try
			{
				var renewed = await queue.RenewHeartbeatAsync(jobId, owner, leaseDuration, CancellationToken.None)
					.ConfigureAwait(false);
				if (!renewed)
				{
					// 租约已丢失（被其他 worker 抢占或状态已改变）——取消主任务的 DispatchAsync。
					_logger.LogWarning(
						"Lease for job {JobId} lost (RenewHeartbeatAsync returned false). Aborting processing.", jobId);
					leaseCts.Cancel();
					return;
				}
			}
			catch (Exception ex)
			{
				// 瞬时错误（网络抖动、连接超时等）——不中止处理，等待下一次续约。
				// 若多次续约失败超过 leaseDuration，lease 真的过期，下一次 RenewHeartbeatAsync 仍会返回 false。
				_logger.LogWarning(ex, "Heartbeat renewal for job {JobId} failed. Will retry on next interval.", jobId);
			}
		}
	}

	/// <summary>
	/// 生成 worker 实例唯一的租约持有者标识。包含机器名、进程 ID 和短 GUID，
	/// 便于在 DB 查询时定位持有租约的 worker 实例。
	/// </summary>
	private static string GenerateOwnerId()
	{
		try
		{
			var machine = Environment.MachineName;
			var pid = Environment.ProcessId;
			var guid = Guid.NewGuid().ToString("N").Substring(0, 12);
			var raw = $"{machine}-p{pid}-{guid}";
			return raw.Length > 60 ? raw.Substring(0, 60) : raw;
		}
		catch
		{
			// Environment.MachineName 在某些受限环境可能抛出——回退到纯 GUID。
			return Guid.NewGuid().ToString("N");
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
