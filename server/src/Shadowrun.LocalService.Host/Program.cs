using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Shadowrun.LocalService.Core;
using Shadowrun.LocalService.Core.Http;
using Shadowrun.LocalService.Core.Protocols;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Host
{
	internal static class Program
	{
		public static int Main(string[] args)
		{
			var options = ParseArgs(args);
			InstallAssemblyResolution(options);
			try
			{
				var asm = typeof(Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData.IStaticData).Assembly;
				Console.WriteLine("[localservice-cs] ServerClientCommons loaded from: {0}", asm.Location);
			}
			catch
			{
				// Ignore.
			}
			var logger = options.DisableFileLogs
				? new RequestLogger(null, null, options.StructuredLogRotationIntervalMinutes, options.StructuredLogRetentionDays)
				: new RequestLogger(options.EventsLogPrefix, options.DiagnosticsLogPrefix, options.StructuredLogRotationIntervalMinutes, options.StructuredLogRetentionDays);
			logger.Reset();
			logger.Log(new
			{
				timestamp = RequestLogger.UtcNowIso(),
				component = "startup",
				eventName = "host-start",
				message = "local service host starting",
				host = options.Host,
				port = options.Port,
				aplayPort = options.APlayPort,
				photonPort = options.PhotonPort,
				persistenceBackend = options.UseSqlite ? "Sqlite" : "Json",
				sqliteDatabasePath = options.UseSqlite ? options.SqliteDatabasePath : null,
				rotationIntervalMinutes = options.StructuredLogRotationIntervalMinutes,
				retentionDays = options.StructuredLogRetentionDays,
				runtime = ".NET Framework 4.8",
			});

			Console.WriteLine("[localservice-cs] listening on http://{0}:{1}", options.Host, options.Port);
			Console.WriteLine("[localservice-cs] APlay TCP stub on {0}:{1}", options.Host, options.APlayPort);
			Console.WriteLine("[localservice-cs] PhotonProxy TCP stub on {0}:{1}", options.Host, options.PhotonPort);
			if (options.DisableFileLogs)
			{
				Console.WriteLine("[localservice-cs] structured logs: (disabled)");
			}
			else
			{
				Console.WriteLine("[localservice-cs] events log prefix: {0}", options.EventsLogPrefix);
				Console.WriteLine("[localservice-cs] diagnostics log prefix: {0}", options.DiagnosticsLogPrefix);
				Console.WriteLine("[localservice-cs] log rotation: {0} minutes", options.StructuredLogRotationIntervalMinutes);
				Console.WriteLine("[localservice-cs] log retention: {0} day(s)", options.StructuredLogRetentionDays);
			}
			Console.WriteLine("[localservice-cs] chat admin config: {0}", options.ChatAdminConfigPath);

			var stopEvent = new ManualResetEvent(false);
			Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs eventArgs)
			{
				eventArgs.Cancel = true;
				stopEvent.Set();
			};

			var userStore = new LocalUserStore(options, logger);
			userStore.RunDisplayNameFormatMigrationOnStartup();
			var sessionIdentityMap = new ExpiringSessionIdentityMap();
			var characterStatePushBroker = new CharacterStatePushBroker();
			var hubPresenceRegistry = new HubPresenceRegistry();
			var httpServer = new HttpStubServer(options, logger, userStore, sessionIdentityMap, null);
			var aplayStub = new APlayTcpStub(options, logger, userStore, sessionIdentityMap, characterStatePushBroker, hubPresenceRegistry);
			var photonStub = new PhotonProxyTcpStub(options, logger, userStore, sessionIdentityMap, characterStatePushBroker, hubPresenceRegistry);

			Exception httpError = null;
			Exception aplayError = null;
			Exception photonError = null;

			var httpThread = new Thread(delegate ()
			{
				try { httpServer.Run(stopEvent); }
				catch (Exception ex) { httpError = ex; stopEvent.Set(); }
			});

			var aplayThread = new Thread(delegate ()
			{
				try { aplayStub.Run(stopEvent); }
				catch (Exception ex) { aplayError = ex; stopEvent.Set(); }
			});

			var photonThread = new Thread(delegate ()
			{
				try { photonStub.Run(stopEvent); }
				catch (Exception ex) { photonError = ex; stopEvent.Set(); }
			});

			httpThread.IsBackground = true;
			aplayThread.IsBackground = true;
			photonThread.IsBackground = true;

			httpThread.Start();
			aplayThread.Start();
			photonThread.Start();

			stopEvent.WaitOne();

			// Give threads a moment to unwind after listener.Stop() triggers.
			httpThread.Join(3000);
			aplayThread.Join(3000);
			photonThread.Join(3000);

			var ex0 = httpError ?? aplayError ?? photonError;
			if (ex0 != null)
			{
				logger.Log(new
				{
					timestamp = RequestLogger.UtcNowIso(),
					component = "fatal",
					eventName = "service-thread-faulted",
					level = "error",
					message = "service thread faulted",
					error = ex0.Message,
					errorType = ex0.GetType().FullName,
				});
				Console.Error.WriteLine("[localservice-cs] fatal: {0}", ex0.Message);
				return 1;
			}

			return 0;
		}

		private static void InstallAssemblyResolution(LocalServiceOptions options)
		{
			// LocalService is a standalone .NET process, not the Unity player.
			// Ensure we resolve Cliffhanger/SRO dependencies from known locations.
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var depsDir = Path.Combine(baseDir, "Dependencies");
			var portableDepsDir = Path.Combine(Path.Combine(baseDir, "Resources"), "Dependencies");
			var gameManagedDir = options != null && !string.IsNullOrEmpty(options.GameRootDir)
				? Path.Combine(Path.Combine(options.GameRootDir, "Shadowrun_Data"), "Managed")
				: null;

			var probeDirs = new[] { baseDir, depsDir, portableDepsDir, gameManagedDir };

			AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs eventArgs)
			{
				try
				{
					var simpleName = new AssemblyName(eventArgs.Name).Name + ".dll";
					for (var i = 0; i < probeDirs.Length; i++)
					{
						var dir = probeDirs[i];
						if (string.IsNullOrEmpty(dir))
						{
							continue;
						}
						var candidate = Path.Combine(dir, simpleName);
						if (File.Exists(candidate))
						{
							return Assembly.LoadFrom(candidate);
						}
					}
				}
				catch
				{
					// Ignore and fall through.
				}
				return null;
			};

			// Preload the most important assemblies early, so types like IStaticData can be resolved consistently.
			PreloadIfExists(probeDirs, "Cliffhanger.Core.Compatibility.dll");
			PreloadIfExists(probeDirs, "SRO.Core.Compatibility.dll");
			PreloadIfExists(probeDirs, "Cliffhanger.GameLogic.dll");
			PreloadIfExists(probeDirs, "Cliffhanger.SRO.ServerClientCommons.dll");
			PreloadIfExists(probeDirs, "JsonFx.Json.dll");
			PreloadIfExists(probeDirs, "Ionic.Zip.dll");
			PreloadIfExists(probeDirs, "Mono.Data.Sqlite.dll");
		}

		private static void PreloadIfExists(string[] probeDirs, string dllName)
		{
			for (var i = 0; i < probeDirs.Length; i++)
			{
				var dir = probeDirs[i];
				if (string.IsNullOrEmpty(dir))
				{
					continue;
				}
				var path = Path.Combine(dir, dllName);
				if (File.Exists(path))
				{
					try
					{
						Assembly.LoadFrom(path);
					}
					catch
					{
					}
					return;
				}
			}
		}

		private static LocalServiceOptions ParseArgs(string[] args)
		{
			var host = "0.0.0.0";
			var port = 80;
			var aplayPort = 5055;
			var photonPort = 4530;
			var noFileLogs = false;
			var useSqlite = false;
			var migrateJsonToSqlite = false;
			string sqliteDbPath = null;

			for (var i = 0; i < args.Length; i++)
			{
				var arg = args[i] ?? string.Empty;
				if (string.Equals(arg, "--no-file-logs", StringComparison.OrdinalIgnoreCase))
				{
					noFileLogs = true;
					continue;
				}
				if (string.Equals(arg, "--use-sqlite", StringComparison.OrdinalIgnoreCase))
				{
					useSqlite = true;
					continue;
				}
				if (string.Equals(arg, "--migrate-json-to-sqlite", StringComparison.OrdinalIgnoreCase))
				{
					migrateJsonToSqlite = true;
					continue;
				}
				if (string.Equals(arg, "--sqlite-db-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					sqliteDbPath = args[++i];
					continue;
				}
				if (string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					host = args[++i];
					continue;
				}
				if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					int parsedPort;
					if (int.TryParse(args[++i], out parsedPort))
					{
						port = parsedPort;
					}
					continue;
				}
				if (string.Equals(arg, "--aplay-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					int parsedAPlayPort;
					if (int.TryParse(args[++i], out parsedAPlayPort))
					{
						aplayPort = parsedAPlayPort;
					}
					continue;
				}
				if (string.Equals(arg, "--photon-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					int parsedPhotonPort;
					if (int.TryParse(args[++i], out parsedPhotonPort))
					{
						photonPort = parsedPhotonPort;
					}
					continue;
				}
				if (string.Equals(arg, "--chat-admins", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					// Deprecated: admin accounts are now loaded from options.ChatAdminConfigPath.
					i++;
					continue;
				}
			}

			var options = new LocalServiceOptions();
			options.Host = host;
			options.Port = port;
			options.APlayPort = aplayPort;
			options.PhotonPort = photonPort;
			options.DisableFileLogs = noFileLogs;
			options.UseSqlite = useSqlite;
			options.MigrateJsonToSqlite = migrateJsonToSqlite;
			if (!string.IsNullOrEmpty(sqliteDbPath))
			{
				options.SqliteDatabasePath = sqliteDbPath;
			}
			return options;
		}
	}

}
