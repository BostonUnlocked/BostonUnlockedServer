using System;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using Shadowrun.LocalService.Core;
using Shadowrun.LocalService.Core.Http;
using Shadowrun.LocalService.Core.Protocols;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Host
{
	internal static class Program
	{
		private const string ServiceFlag = "--service";

		public static int Main(string[] args)
		{
			var options = ParseArgs(args);
			if (ShouldRunAsWindowsService(args))
			{
				if (Environment.UserInteractive)
				{
					Console.Error.WriteLine("[localservice-cs] '{0}' can only be used under Service Control Manager.", ServiceFlag);
					return 2;
				}

				ServiceBase.Run(new[] { new LocalServiceWindowsService(options) });
				return 0;
			}

			var runtime = new LocalServiceRuntime(options, true);
			return runtime.RunUntilStoppedByConsole();
		}

		private static bool ShouldRunAsWindowsService(string[] args)
		{
			if (HasFlag(args, ServiceFlag))
			{
				return true;
			}

			return !Environment.UserInteractive;
		}

		private static bool HasFlag(string[] args, string flag)
		{
			if (args == null)
			{
				return false;
			}

			for (var i = 0; i < args.Length; i++)
			{
				if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		internal static void InstallAssemblyResolution(LocalServiceOptions options)
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
			var useFixedMissionSeeds = false;
			var useSqlite = true;
			var migrateJsonToSqlite = false;
			string sqliteDbPath = null;

			for (var i = 0; i < args.Length; i++)
			{
				var arg = args[i] ?? string.Empty;
				if (string.Equals(arg, ServiceFlag, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
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
				if (string.Equals(arg, "--use-json", StringComparison.OrdinalIgnoreCase))
				{
					useSqlite = false;
					continue;
				}
				if (string.Equals(arg, "--fixed-seed", StringComparison.OrdinalIgnoreCase))
				{
					useFixedMissionSeeds = true;
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
			options.UseFixedMissionSeeds = useFixedMissionSeeds;
			options.UseSqlite = useSqlite;
			options.MigrateJsonToSqlite = migrateJsonToSqlite;
			if (!string.IsNullOrEmpty(sqliteDbPath))
			{
				options.SqliteDatabasePath = sqliteDbPath;
			}
			return options;
		}
	}

	internal sealed class LocalServiceWindowsService : ServiceBase
	{
		private readonly LocalServiceOptions _options;
		private LocalServiceRuntime _runtime;
		private Thread _watchThread;

		public LocalServiceWindowsService(LocalServiceOptions options)
		{
			_options = options;
			ServiceName = "ShadowrunLocalService";
			CanStop = true;
			CanPauseAndContinue = false;
			AutoLog = false;
		}

		protected override void OnStart(string[] args)
		{
			_runtime = new LocalServiceRuntime(_options, false);
			_runtime.Start();

			_watchThread = new Thread(delegate ()
			{
				var exitCode = _runtime.WaitForStop();
				if (exitCode != 0)
				{
					try
					{
						ExitCode = exitCode;
					}
					catch
					{
					}

					try
					{
						Stop();
					}
					catch
					{
					}
				}
			});
			_watchThread.IsBackground = true;
			_watchThread.Start();
		}

		protected override void OnStop()
		{
			if (_runtime != null)
			{
				_runtime.Stop();
				_runtime.WaitForStop();
			}
		}
	}

	internal sealed class LocalServiceRuntime
	{
		private readonly LocalServiceOptions _options;
		private readonly bool _writeConsole;

		private RequestLogger _logger;
		private ManualResetEvent _stopEvent;
		private Thread _httpThread;
		private Thread _aplayThread;
		private Thread _photonThread;
		private Exception _httpError;
		private Exception _aplayError;
		private Exception _photonError;
		private bool _started;

		public LocalServiceRuntime(LocalServiceOptions options, bool writeConsole)
		{
			_options = options;
			_writeConsole = writeConsole;
		}

		public int RunUntilStoppedByConsole()
		{
			ConsoleCancelEventHandler cancelHandler = delegate (object sender, ConsoleCancelEventArgs eventArgs)
			{
				eventArgs.Cancel = true;
				Stop();
			};

			Console.CancelKeyPress += cancelHandler;
			try
			{
				Start();
				return WaitForStop();
			}
			finally
			{
				Console.CancelKeyPress -= cancelHandler;
			}
		}

		public void Start()
		{
			if (_started)
			{
				return;
			}

			Program.InstallAssemblyResolution(_options);

			if (_writeConsole)
			{
				try
				{
					var asm = typeof(Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData.IStaticData).Assembly;
					Console.WriteLine("[localservice-cs] ServerClientCommons loaded from: {0}", asm.Location);
				}
				catch
				{
				}
			}

			_logger = _options.DisableFileLogs
				? new RequestLogger(null, null, null, _options.StructuredLogRotationIntervalMinutes, _options.StructuredLogRetentionDays)
				: new RequestLogger(_options.EventsLogPrefix, _options.DiagnosticsLogPrefix, _options.PlayerBugReportsLogPrefix, _options.StructuredLogRotationIntervalMinutes, _options.StructuredLogRetentionDays);

			_logger.Reset();
			_logger.Log(new
			{
				timestamp = RequestLogger.UtcNowIso(),
				component = "startup",
				eventName = "host-start",
				message = "local service host starting",
				host = _options.Host,
				port = _options.Port,
				aplayPort = _options.APlayPort,
				photonPort = _options.PhotonPort,
				persistenceBackend = _options.UseSqlite ? "Sqlite" : "Json",
				sqliteDatabasePath = _options.UseSqlite ? _options.SqliteDatabasePath : null,
				rotationIntervalMinutes = _options.StructuredLogRotationIntervalMinutes,
				retentionDays = _options.StructuredLogRetentionDays,
				missionSeedMode = _options.UseFixedMissionSeeds ? "fixed" : "random",
				runtime = ".NET Framework 4.8",
				hostMode = _writeConsole ? "console" : "windows-service",
			});

			if (_writeConsole)
			{
				Console.WriteLine("[localservice-cs] listening on http://{0}:{1}", _options.Host, _options.Port);
				Console.WriteLine("[localservice-cs] APlay TCP stub on {0}:{1}", _options.Host, _options.APlayPort);
				Console.WriteLine("[localservice-cs] PhotonProxy TCP stub on {0}:{1}", _options.Host, _options.PhotonPort);
				if (_options.DisableFileLogs)
				{
					Console.WriteLine("[localservice-cs] structured logs: (disabled)");
				}
				else
				{
					Console.WriteLine("[localservice-cs] events log prefix: {0}", _options.EventsLogPrefix);
					Console.WriteLine("[localservice-cs] diagnostics log prefix: {0}", _options.DiagnosticsLogPrefix);
					Console.WriteLine("[localservice-cs] player bug log prefix: {0}", _options.PlayerBugReportsLogPrefix);
					Console.WriteLine("[localservice-cs] log rotation: {0} minutes", _options.StructuredLogRotationIntervalMinutes);
					Console.WriteLine("[localservice-cs] log retention: {0} day(s)", _options.StructuredLogRetentionDays);
				}
				Console.WriteLine("[localservice-cs] chat admin config: {0}", _options.ChatAdminConfigPath);
				Console.WriteLine("[localservice-cs] mission seed mode: {0}", _options.UseFixedMissionSeeds ? "fixed" : "random");
			}

			_stopEvent = new ManualResetEvent(false);

			var userStore = new LocalUserStore(_options, _logger);
			userStore.RunDisplayNameFormatMigrationOnStartup();
			var sessionIdentityMap = new ExpiringSessionIdentityMap();
			var characterStatePushBroker = new CharacterStatePushBroker();
			var hubPresenceRegistry = new HubPresenceRegistry();
			var httpServer = new HttpStubServer(_options, _logger, userStore, sessionIdentityMap, null, hubPresenceRegistry);
			var aplayStub = new APlayTcpStub(_options, _logger, userStore, sessionIdentityMap, characterStatePushBroker, hubPresenceRegistry);
			var photonStub = new PhotonProxyTcpStub(_options, _logger, userStore, sessionIdentityMap, characterStatePushBroker, hubPresenceRegistry);

			_httpThread = new Thread(delegate ()
			{
				try { httpServer.Run(_stopEvent); }
				catch (Exception ex) { _httpError = ex; _stopEvent.Set(); }
			});

			_aplayThread = new Thread(delegate ()
			{
				try { aplayStub.Run(_stopEvent); }
				catch (Exception ex) { _aplayError = ex; _stopEvent.Set(); }
			});

			_photonThread = new Thread(delegate ()
			{
				try { photonStub.Run(_stopEvent); }
				catch (Exception ex) { _photonError = ex; _stopEvent.Set(); }
			});

			_httpThread.IsBackground = true;
			_aplayThread.IsBackground = true;
			_photonThread.IsBackground = true;

			_httpThread.Start();
			_aplayThread.Start();
			_photonThread.Start();

			_started = true;
		}

		public void Stop()
		{
			if (_stopEvent != null)
			{
				_stopEvent.Set();
			}
		}

		public int WaitForStop()
		{
			if (_stopEvent == null)
			{
				return 0;
			}

			_stopEvent.WaitOne();
			JoinIfPresent(_httpThread);
			JoinIfPresent(_aplayThread);
			JoinIfPresent(_photonThread);

			var ex0 = _httpError ?? _aplayError ?? _photonError;
			if (ex0 != null)
			{
				if (_logger != null)
				{
					_logger.Log(new
					{
						timestamp = RequestLogger.UtcNowIso(),
						component = "fatal",
						eventName = "service-thread-faulted",
						level = "error",
						message = "service thread faulted",
						error = ex0.Message,
						errorType = ex0.GetType().FullName,
					});
				}

				if (_writeConsole)
				{
					Console.Error.WriteLine("[localservice-cs] fatal: {0}", ex0.Message);
				}
				return 1;
			}

			return 0;
		}

		private static void JoinIfPresent(Thread thread)
		{
			if (thread == null)
			{
				return;
			}

			// Give listener loops a short window to unwind after stop signal.
			thread.Join(3000);
		}
	}

}
