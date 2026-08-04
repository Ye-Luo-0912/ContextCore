using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>托管后台服务，持续轮询 ContextCore 作业队列并分发给对应处理器。</summary>
/// <remarks>
/// 当队列实现 <see cref="ILeasedJobQueue"/> 时（如 Postgres），worker 切换到租约路径：
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

	/// <summary>活跃作业租约注册表（jobId → 条目），供共享批量心跳循环续约。</summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobLeaseEntry> _jobLeases =
		new(System.StringComparer.Ordinal);

	private readonly object _heartbeatLock = new();
	private Task? _heartbeatLoopTask;
	private CancellationTokenSource? _heartbeatLoopCts;

	public ContextJobWorker(
		IServiceProvider services,
		IOptions<JobWorkerOptions> options,
		ILogger<ContextJobWorker> logger)
	{
		_services = services;
		_options = options;
		_logger = logger;
	}

	/// <summary>共享心跳注册表条目：jobId + owner + 作业取消源 + 最后确认过期时间（本地 watchdog）。</summary>
	private sealed class JobLeaseEntry
	{
		public required string JobId { get; init; }
		public required string Owner { get; init; }
		public required CancellationTokenSource LeaseCts { get; init; }
		public long LastConfirmedExpiresTicks;
		public int ConsecutiveFailures;
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
		// 租约配置。仅当队列实现 ILeasedJobQueue 时生效；否则走 Dequeue 路径。
		var leaseDuration = _options.Value.LeaseDuration;
		var heartbeatInterval = _options.Value.HeartbeatInterval;
		var owner = GenerateOwnerId();
		// SemaphoreSlim 控制最大并发槽位，PostgreSQL 队列使用 SELECT FOR UPDATE SKIP LOCKED
		// 确保多个并发槽位（或多个 worker 实例）不会重复消费同一作业。
		using var semaphore = new SemaphoreSlim(concurrency, concurrency);
		_logger.LogInformation(
			"Context job worker started. PollInterval={PollInterval}ms, Concurrency={Concurrency}, Owner={Owner}, LeaseDuration={LeaseDuration}, HeartbeatInterval={HeartbeatInterval}.",
			delay.TotalMilliseconds, concurrency, owner, leaseDuration, heartbeatInterval);

		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				// 等待空闲槽位；若所有槽位占满则休眠 poll interval 再检查。
				if (!await semaphore.WaitAsync(delay, stoppingToken).ConfigureAwait(false))
				{
					continue;
				}

				// 每轮创建 scope 完成领取；处理阶段由 ProcessJobAsync 为每个作业自建 scope。
				// 所有涉及服务均为 Singleton 注册（队列/dispatcher/event sink），
				// 领取与处理跨 scope 解析到的实例一致。
				IReadOnlyList<ContextJob> jobs;
				var scope = _services.CreateScope();
				try
				{
					var queue = scope.ServiceProvider.GetRequiredService<IContextJobQueue>();
					// 检测队列是否支持租约语义。Postgres 实现 ILeasedJobQueue；InMemory/File 不实现。
					var leasedQueue = queue as ILeasedJobQueue;
					if (leasedQueue is not null)
					{
						// 批量领取：一次性取满空闲槽位（至少 1），按 workspace 公平分配。
						var available = Math.Max(1, semaphore.CurrentCount);
						var take = Math.Min(concurrency, available);
						jobs = await leasedQueue.AcquireLeaseBatchAsync(
								owner, leaseDuration, take, _options.Value.MaxPerWorkspaceClaim, stoppingToken)
							.ConfigureAwait(false);
					}
					else
					{
						var single = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
						jobs = single is null ? Array.Empty<ContextJob>() : new[] { single };
					}
				}
				finally
				{
					scope.Dispose();
				}

				if (jobs.Count == 0)
				{
					// 归还探查槽位；队列为空时短暂休眠，避免空转占满 CPU。
					semaphore.Release();
					await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
					continue;
				}

				// 归还探查槽位，再为每个领取到的作业预留独立槽位（由处理任务释放）。
				semaphore.Release();
				foreach (var job in jobs)
				{
					await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);
					// 异步执行作业，不阻塞轮询循环，允许同时处理多个作业。
					_ = Task.Run(() => ProcessJobAsync(job, owner, leaseDuration, semaphore, stoppingToken), stoppingToken);
				}
			}
		}
		finally
		{
			// 停止共享批量心跳循环（worker 退出时不再续约）
			await StopHeartbeatLoopAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// 处理单个作业：租约路径下启动 heartbeat 续约，分发执行后 Ack/Nack。
	/// 每个作业自建 scope（与领取 scope 分离），保证 scoped 服务生命周期与作业一致。
	/// </summary>
	private async Task ProcessJobAsync(
		ContextJob job,
		string owner,
		TimeSpan leaseDuration,
		SemaphoreSlim semaphore,
		CancellationToken stoppingToken)
	{
		using var scope = _services.CreateScope();
		var queue = scope.ServiceProvider.GetRequiredService<IContextJobQueue>();
		// 租约路径下将作业注册到共享批量心跳，周期性续约。
		// leaseCts 链接到 stoppingToken——host 关闭时也会取消租约。
		// 共享心跳续约失败时取消 leaseCts，让 DispatchAsync 抛出 OperationCanceledException，
		// 主任务据此识别租约丢失并跳过 Ack/Nack。
		var leasedQueue = queue as ILeasedJobQueue;
		CancellationTokenSource? leaseCts = null;

		if (leasedQueue is not null)
		{
			leaseCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			RegisterJobLease(job.JobId, owner, leaseCts);
		}

		var effectiveToken = leaseCts?.Token ?? stoppingToken;

		try
		{
			var dispatcher = scope.ServiceProvider.GetRequiredService<IContextJobDispatcher>();
			var eventSink = scope.ServiceProvider.GetRequiredService<IContextEventSink>();
			await dispatcher.DispatchAsync(job, effectiveToken).ConfigureAwait(false);
			await queue.AckAsync(job.JobId, stoppingToken).ConfigureAwait(false);
			// Event Sink fail-open——作业已成功并 Ack，sink 发射失败不得触发 Nack/error 路径。
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
			// 租约丢失——RenewHeartbeatAsync 返回 false，heartbeat 任务已取消 leaseCts。
			// 不 Ack/Nack：保留 state='Running'，lease_expires_at 已过期，
			// 其他 worker 的 AcquireLeaseBatchAsync 会通过 (state='Running' AND lease_expires_at <= now) 抢占恢复。
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
				var eventSink2 = scope.ServiceProvider.GetRequiredService<IContextEventSink>();
				await queue.NackAsync(job.JobId, ex.Message, CancellationToken.None).ConfigureAwait(false);
				await EmitAsync(eventSink2, job, ContextEventLevel.Error, ex.Message, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception nackEx)
			{
				_logger.LogError(nackEx, "Failed to nack job {JobId}.", job.JobId);
			}
		}
		finally
		{
			// 从共享心跳注册表移除（停止续约），并释放 leaseCts。
			// leaseCts.Cancel() 让依赖它的 DispatchAsync 感知取消；此处作业已结束，仅为释放。
			UnregisterJobLease(job.JobId);
			if (leaseCts is not null)
			{
				leaseCts.Cancel();
				leaseCts.Dispose();
			}
			semaphore.Release();
		}
	}

	/// <summary>
	/// 将作业注册到共享批量心跳注册表；懒启动共享心跳循环（首个注册时）。
	/// 续约失败（租约丢失）时由循环取消 <paramref name="leaseCts"/>。
	/// </summary>
	private void RegisterJobLease(string jobId, string owner, CancellationTokenSource leaseCts)
	{
		_jobLeases[jobId] = new JobLeaseEntry
		{
			JobId = jobId,
			Owner = owner,
			LeaseCts = leaseCts,
			LastConfirmedExpiresTicks = DateTimeOffset.UtcNow.Add(
				_options.Value.LeaseDuration > TimeSpan.Zero ? _options.Value.LeaseDuration : TimeSpan.FromMinutes(10)).UtcTicks
		};

		lock (_heartbeatLock)
		{
			if (_heartbeatLoopTask is null || _heartbeatLoopTask.IsCompleted)
			{
				_heartbeatLoopCts?.Dispose();
				_heartbeatLoopCts = new CancellationTokenSource();
				_heartbeatLoopTask = RunBatchHeartbeatLoopAsync(_heartbeatLoopCts.Token);
			}
		}
	}

	/// <summary>从共享批量心跳注册表移除作业（作业结束后停止续约）。</summary>
	private void UnregisterJobLease(string jobId)
	{
		_jobLeases.TryRemove(jobId, out _);
	}

	/// <summary>停止共享批量心跳循环（worker 退出时调用）。</summary>
	private async Task StopHeartbeatLoopAsync()
	{
		lock (_heartbeatLock)
		{
			_heartbeatLoopCts?.Cancel();
		}
		if (_heartbeatLoopTask is not null)
		{
			try { await _heartbeatLoopTask.ConfigureAwait(false); }
			catch { /* 循环异常已在内部记录，此处忽略 */ }
		}
		lock (_heartbeatLock)
		{
			_heartbeatLoopCts?.Dispose();
			_heartbeatLoopCts = null;
			_heartbeatLoopTask = null;
		}
	}

	/// <summary>
	/// 共享批量心跳循环：每 <see cref="JobWorkerOptions.HeartbeatInterval"/> 周期
	/// 通过一次 <see cref="ILeasedJobQueue.RenewHeartbeatBatchAsync"/> 续约全部活跃作业，
	/// 替代"每个作业一个独立续约任务 + 每次 DB 往返"的模式（N 次往返 → 1 次）。
	/// 失败语义与旧逐条心跳一致：续约失败（租约被抢占/状态改变）→ 取消对应作业；
	/// 连续异常超过阈值 → 取消全部活跃作业（数据库不可达时防止无租约执行副作用）。
	/// </summary>
	private async Task RunBatchHeartbeatLoopAsync(CancellationToken cancellationToken)
	{
		var heartbeatInterval = _options.Value.HeartbeatInterval > TimeSpan.Zero
			? _options.Value.HeartbeatInterval
			: TimeSpan.FromSeconds(15);
		var leaseDuration = _options.Value.LeaseDuration > TimeSpan.Zero
			? _options.Value.LeaseDuration
			: TimeSpan.FromMinutes(10);
		const int MaxConsecutiveFailures = 3;

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			var entries = _jobLeases.Values.ToList();
			if (entries.Count == 0)
			{
				continue;
			}

			var now = DateTimeOffset.UtcNow;
			var cancelSet = new HashSet<string>(StringComparer.Ordinal);

			// 本地 watchdog：最后一次确认的租约已过期 → 取消对应作业（不发起续约）
			foreach (var entry in entries)
			{
				if (now.UtcTicks >= Interlocked.Read(ref entry.LastConfirmedExpiresTicks))
				{
					_logger.LogWarning(
						"Job {JobId} 本地确认的租约已过期（ExpiresAt={ExpiresAt}），取消处理。",
						entry.JobId, new DateTimeOffset(entry.LastConfirmedExpiresTicks, TimeSpan.Zero));
					CancelJobLease(entry);
					cancelSet.Add(entry.JobId);
				}
			}

			// 解析队列（Singleton 注册；循环在 worker 生命周期内解析同一实例）
			ILeasedJobQueue? queue = null;
			try
			{
				using var scope = _services.CreateScope();
				queue = scope.ServiceProvider.GetService<IContextJobQueue>() as ILeasedJobQueue;
			}
			catch
			{
				// 解析失败按瞬时错误处理，下周期重试
			}
			if (queue is null)
			{
				continue;
			}

			var toRenew = entries
				.Where(e => !cancelSet.Contains(e.JobId))
				.Select(e => new JobLeaseRenewal { JobId = e.JobId, Owner = e.Owner })
				.ToList();
			if (toRenew.Count == 0)
			{
				continue;
			}

			try
			{
				var failed = await queue.RenewHeartbeatBatchAsync(toRenew, leaseDuration, cancellationToken)
					.ConfigureAwait(false);
				foreach (var entry in entries)
				{
					if (cancelSet.Contains(entry.JobId))
					{
						continue;
					}
					if (failed.Contains(entry.JobId, StringComparer.Ordinal))
					{
						// 租约已丢失（被其他 worker 抢占或状态已改变）——取消主任务的 DispatchAsync。
						_logger.LogWarning(
							"Job {JobId} 租约续约失败（被抢占或状态已改变），中止处理。", entry.JobId);
						CancelJobLease(entry);
					}
					else
					{
						// 续约成功 → 重置连续异常计数 + 更新最后确认的过期时间
						Interlocked.Exchange(ref entry.ConsecutiveFailures, 0);
						Interlocked.Exchange(
							ref entry.LastConfirmedExpiresTicks,
							DateTimeOffset.UtcNow.Add(leaseDuration).UtcTicks);
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch
			{
				// 瞬时错误（网络抖动、连接超时等）——不立即中止；
				// 连续异常超过阈值后取消全部活跃作业（若 lease 真的过期，
				// 下一次批量续约仍会返回失败）。
				foreach (var entry in entries)
				{
					var failures = Interlocked.Increment(ref entry.ConsecutiveFailures);
					if (failures >= MaxConsecutiveFailures)
					{
						_logger.LogError(
							"Job {JobId} 心跳续约连续失败 {Failures} 次，中止处理。", entry.JobId, failures);
						CancelJobLease(entry);
					}
				}
			}
		}
	}

	/// <summary>取消作业处理（租约丢失或本地 watchdog 触发）。</summary>
	private void CancelJobLease(JobLeaseEntry entry)
	{
		try { entry.LeaseCts.Cancel(); }
		catch (ObjectDisposedException) { /* 作业已完成并释放了 leaseCts，忽略 */ }
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
