﻿using Microsoft.Extensions.Options;
using RecurrentTasks;
using SomeDAO.Backend.Data;
using TonLibDotNet;

namespace SomeDAO.Backend.Services
{
	public class SyncTask : IRunnable
	{
		public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
		private static readonly TimeSpan HaveMoreDataInterval = TimeSpan.FromSeconds(3);

		private const int MaxBatch = 100;

		private readonly ILogger logger;
		private readonly ITonClient tonClient;
		private readonly IDbProvider dbProvider;
		private readonly DataParser dataParser;
		private readonly ITask cachedDataTask;

		public SyncTask(ILogger<SyncTask> logger, ITonClient tonClient, IDbProvider dbProvider, IOptions<BackendOptions> options, DataParser dataParser, ITask<CachedData> cachedDataTask)
		{
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.tonClient = tonClient ?? throw new ArgumentNullException(nameof(tonClient));
			this.dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
			this.dataParser = dataParser ?? throw new ArgumentNullException(nameof(dataParser));
			this.cachedDataTask = cachedDataTask;
		}

		public async Task RunAsync(ITask currentTask, IServiceProvider scopeServiceProvider, CancellationToken cancellationToken)
		{
			// Fast retry to init TonClient
			currentTask.Options.Interval = HaveMoreDataInterval;
			await tonClient.InitIfNeeded().ConfigureAwait(false);

			var db = dbProvider.MainDb;

			var counter = 0;
			while (counter < MaxBatch)
			{
				var next = await db.Table<SyncQueueItem>().OrderBy(x => x.SyncAt).FirstOrDefaultAsync();

				if (next == null)
				{
					logger.LogDebug("No [more] data to sync.");
					currentTask.Options.Interval = Interval;
					break;
				}

				var wait = next.SyncAt - DateTimeOffset.UtcNow;
				if (wait > TimeSpan.Zero)
				{
					logger.LogDebug("Next ({Type} #{Index}) sync in {Wait} at {Time}, will wait.", next.EntityType, next.Index, wait, next.SyncAt);
					currentTask.Options.Interval = wait < Interval ? wait : Interval;
					break;
				}

				counter++;

				try
				{
					logger.LogDebug("Sync #{Counter} ({Type} #{Index}) started...", counter, next.EntityType, next.Index);
					var task = next.EntityType switch
					{
						EntityType.Admin => SyncAdmin(next.Index),
						EntityType.User => SyncUser(next.Index),
						EntityType.Order => SyncOrder(next.Index),
						_ => Task.FromResult(DateTimeOffset.MaxValue),
					};

					var lastSync = await task.ConfigureAwait(false);

					var deleted = await db.Table<SyncQueueItem>().Where(x => x.Index == next.Index && x.EntityType == next.EntityType && x.MinLastSync <= lastSync).DeleteAsync();

					if (lastSync == DateTimeOffset.MaxValue)
					{
						logger.LogWarning("Sync #{Counter} ({Type} #{Index}) SKIPPED, deleted {Count} sync item(s) from queue.", counter, next.EntityType, next.Index, deleted);
					}
					else
					{
						logger.LogDebug("Sync #{Counter} ({Type} #{Index}) done, last_sync={LastSync}, deleted {Count} sync item(s) from queue.", counter, next.EntityType, next.Index, lastSync, deleted);
						if (lastSync < next.MinLastSync)
						{
							var delay = GetDelay(next.RetryCount);
							logger.LogWarning("Sync #{Counter} ({Type} #{Index}) sync less than required ({MinSync}), will retry in {Delay}.", counter, next.EntityType, next.Index, next.MinLastSync, delay);
							next.SyncAt = DateTimeOffset.UtcNow + delay;
							next.RetryCount += 1;
							await db.InsertOrReplaceAsync(next).ConfigureAwait(false);
						}
					}
				}
				catch (Exception ex)
				{
					var delay = GetDelay(next.RetryCount);
					logger.LogError(ex, "Sync #{Counter} ({Type} #{Index}) failed, will retry in {Delay}.", counter, next.EntityType, next.Index, delay);
					next.SyncAt = DateTimeOffset.UtcNow + delay;
					next.RetryCount += 1;
					await db.UpdateAsync(next).ConfigureAwait(false);
				}
			}

			if (counter > 0)
			{
				cachedDataTask.TryRunImmediately();
			}
		}

		protected async Task<DateTimeOffset> SyncAdmin(long index)
		{
			var admin = await dbProvider.MainDb.FindAsync<Admin>(index);
			if (admin == null)
			{
				logger.LogWarning("Admin #{Index} was not found, nothing to sync", index);
				return DateTimeOffset.MaxValue;
			}

			await dataParser.UpdateAdmin(admin).ConfigureAwait(false);
			await dbProvider.MainDb.InsertOrReplaceAsync(admin).ConfigureAwait(false);
			return admin.LastSync;
		}

		protected async Task<DateTimeOffset> SyncUser(long index)
		{
			var user = await dbProvider.MainDb.FindAsync<User>(index);
			if (user == null)
			{
				logger.LogWarning("User #{Index} was not found, nothing to sync", index);
				return DateTimeOffset.MaxValue;
			}

			await dataParser.UpdateUser(user).ConfigureAwait(false);
			await dbProvider.MainDb.InsertOrReplaceAsync(user).ConfigureAwait(false);
			return user.LastSync;
		}

		protected async Task<DateTimeOffset> SyncOrder(long index)
		{
			var order = await dbProvider.MainDb.FindAsync<Order>(index);
			if (order == null)
			{
				logger.LogWarning("Order #{Index} was not found, nothing to sync", index);
				return DateTimeOffset.MaxValue;
			}

			await dataParser.UpdateOrder(order).ConfigureAwait(false);
			await dbProvider.MainDb.InsertOrReplaceAsync(order).ConfigureAwait(false);
			return order.LastSync;
		}

		private static TimeSpan GetDelay(int retryCount)
		{
			return retryCount switch
			{
				0 => TimeSpan.FromSeconds(5),
				1 => TimeSpan.FromSeconds(5),
				2 => TimeSpan.FromSeconds(5),
				3 => TimeSpan.FromSeconds(10),
				4 => TimeSpan.FromSeconds(15),
				5 => TimeSpan.FromSeconds(30),
				6 => TimeSpan.FromSeconds(60),
				7 => TimeSpan.FromMinutes(2),
				8 => TimeSpan.FromMinutes(5),
				9 => TimeSpan.FromMinutes(10),
				10 => TimeSpan.FromMinutes(30),
				11 => TimeSpan.FromHours(1),
				12 => TimeSpan.FromHours(4),
				_ => TimeSpan.FromHours(13),
			};
		}
	}
}
