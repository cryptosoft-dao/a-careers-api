namespace SomeDAO.Backend
{
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SomeDAO.Backend.Data;
    using TonLibDotNet;

    public static class Program
    {
        public const string StartAsIndexerArg = "--indexer";

        public static bool InIndexerMode { get; private set; }

        public static async Task Main(string[] args)
        {
            InIndexerMode = args.Contains(StartAsIndexerArg, StringComparer.OrdinalIgnoreCase);

            var host = (InIndexerMode ? CreateIndexerHostBuilder(args) : CreateApiHostBuilder(args)).Build();

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IDbProvider>();
                db.Migrate();

                var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Program));
                CheckMasterAddress(db, logger, scope.ServiceProvider);
                CheckMainnet(db, logger, scope.ServiceProvider);

                var inb = db.MainDb.Find<Settings>(Settings.IGNORE_NOTIFICATIONS_BEFORE);
                if (inb == null)
                {
                    inb = new Settings(Settings.IGNORE_NOTIFICATIONS_BEFORE, DateTimeOffset.UtcNow);
                    db.MainDb.Insert(inb);
                }
            }

            await host.RunAsync();
        }

        public static IHostBuilder CreateApiHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(o => o.AddSystemdConsole())
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<StartupApi>();
                });

        public static IHostBuilder CreateIndexerHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(o => o.AddSystemdConsole())
                .UseConsoleLifetime()
                .ConfigureServices(StartupIndexer.Configure);

        private static void CheckMasterAddress(IDbProvider db, ILogger logger, IServiceProvider serviceProvider)
        {
            var backopt = serviceProvider.GetRequiredService<IOptions<BackendOptions>>();
            var master = backopt.Value.MasterAddress;

            if (string.IsNullOrWhiteSpace(master))
            {
                throw new InvalidOperationException("Master contract not set (in appsettings file).");
            }

            var adr = db.MainDb.Find<Settings>(Settings.MASTER_ADDRESS);
            if (adr == null)
            {
                adr = new Settings(Settings.MASTER_ADDRESS, master);
                db.MainDb.Insert(adr);
            }
            else if (!string.Equals(adr.StringValue, master, StringComparison.Ordinal))
            {
                logger.LogCritical("Master contract mismatch: saved {Address}, configured {Address}. Erase db to start with new master address!", adr.StringValue, master);
                throw new InvalidOperationException("Master contract changed");
            }

            logger.LogInformation("Master contract address: {Address}", master);
        }

        private static void CheckMainnet(IDbProvider db, ILogger logger, IServiceProvider serviceProvider)
        {
            var tonopt = serviceProvider.GetRequiredService<IOptions<TonOptions>>();
            var mainnet = tonopt.Value.UseMainnet;

            var mnet = db.MainDb.Find<Settings>(Settings.IN_MAINNET);
            if (mnet == null)
            {
                mnet = new Settings(Settings.IN_MAINNET, mainnet);
                db.MainDb.Insert(mnet);
            }
            else if (mnet.BoolValue != mainnet)
            {
                logger.LogError("Net type mismatch: saved {Address}, configured {Address}. Erase db to start with new net type!", mnet.BoolValue!.Value ? "MAINnet" : "TESTnet", mainnet ? "MAINnet" : "TESTnet");
                throw new InvalidOperationException("Net type changed");
            }

            logger.LogInformation("Net type: {Value}", mainnet ? "MAINnet" : "TESTnet");
        }
    }
}
