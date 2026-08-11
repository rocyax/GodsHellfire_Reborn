using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Diagnostics;

/// <summary>
/// Explicitly armed, single-player-only diagnostic session. All file writes are
/// command driven except while a short probe is pending; an inactive session has
/// no scanning or periodic I/O.
/// </summary>
internal static class DiagnosticSession
{
	private const int ChallengeLifetimeSeconds = 120;
	private const string ReportFolderName = "GHR-TestReports";

	private static readonly object FileLock = new();
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly JsonSerializerOptions CompactJson = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};
	private static readonly JsonSerializerOptions IndentedJson = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private static string enableChallenge;
	private static DateTimeOffset enableChallengeExpiresUtc;
	private static DateTimeOffset startedUtc;
	private static string eventsPath;
	private static int eventCount;
	private static string lastIOError;

	internal static bool Enabled { get; private set; }
	internal static string SessionId { get; private set; }
	internal static string ReportDirectory { get; private set; }
	internal static string LastReportDirectory { get; private set; }
	internal static int EventCount => eventCount;
	internal static string LastIOError => lastIOError;

	private static Mod ModInstance => ModContent.GetInstance<global::GodsHellfire_Reborn.GodsHellfire_Reborn>();

	internal static string IssueEnableChallenge()
	{
		if (Enabled)
			return null;

		enableChallenge = Guid.NewGuid().ToString("N")[..8];
		enableChallengeExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(ChallengeLifetimeSeconds);
		return enableChallenge;
	}

	internal static bool TryStart(string challenge, Player player, out string message)
	{
		if (Enabled)
		{
			message = $"Diagnostics are already enabled. Report: {ReportDirectory}";
			return true;
		}

		if (Main.netMode != NetmodeID.SinglePlayer || Main.gameMenu || player == null || player.whoAmI != Main.myPlayer)
		{
			message = "Diagnostics can only be enabled by the local player inside a single-player world.";
			return false;
		}

		if (string.IsNullOrWhiteSpace(enableChallenge) || DateTimeOffset.UtcNow > enableChallengeExpiresUtc)
		{
			ClearChallenge();
			message = "The enable challenge is missing or expired. Run /ghrdiag enable again.";
			return false;
		}

		if (!string.Equals(challenge, enableChallenge, StringComparison.OrdinalIgnoreCase))
		{
			message = "The enable challenge did not match.";
			return false;
		}

		try
		{
			startedUtc = DateTimeOffset.UtcNow;
			SessionId = $"{startedUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
			string root = Path.Combine(Main.SavePath, ReportFolderName);
			ReportDirectory = Path.Combine(root, SessionId);
			Directory.CreateDirectory(ReportDirectory);
			eventsPath = Path.Combine(ReportDirectory, "events.jsonl");
			File.WriteAllText(eventsPath, string.Empty, Utf8NoBom);

			eventCount = 0;
			lastIOError = null;
			Enabled = true;
			LastReportDirectory = ReportDirectory;
			ClearChallenge();

			Record("session", "started", new
			{
				player = DiagnosticData.CapturePlayer(player),
				reportDirectory = ReportDirectory
			});
			WriteSummary("active");
			ModInstance.Logger.Info($"[GHR-TEST] Diagnostic session {SessionId} started: {ReportDirectory}");
			message = $"Diagnostics enabled. Session: {SessionId}\nReport: {ReportDirectory}";
			return true;
		}
		catch (Exception exception)
		{
			Enabled = false;
			lastIOError = $"{exception.GetType().FullName}: {exception.Message}";
			TryLogError("Could not start a diagnostic session.", exception);
			message = $"Could not create the diagnostic report: {lastIOError}";
			return false;
		}
	}

	internal static bool Record(string category, string outcome, object data = null)
	{
		if (!Enabled || string.IsNullOrWhiteSpace(eventsPath))
			return false;

		try
		{
			var entry = new DiagnosticEventEntry
			{
				SchemaVersion = 1,
				TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
				SessionId = SessionId,
				GameUpdateCount = Main.GameUpdateCount,
				Category = category,
				Outcome = outcome,
				Data = data
			};

			string json = JsonSerializer.Serialize(entry, CompactJson);
			lock (FileLock)
			{
				File.AppendAllText(eventsPath, json + Environment.NewLine, Utf8NoBom);
				eventCount++;
			}

			ModInstance.Logger.Info($"[GHR-TEST] {category}: {outcome}");
			return true;
		}
		catch (Exception exception)
		{
			lastIOError = $"{exception.GetType().FullName}: {exception.Message}";
			TryLogError($"Could not write diagnostic event '{category}'.", exception);
			return false;
		}
	}

	internal static string CaptureSnapshot(Player player, string label)
	{
		DiagnosticSnapshot snapshot = DiagnosticData.CaptureSnapshot(player, label);
		Record("snapshot", "captured", snapshot);
		WriteSummary("active");
		return $"Snapshot '{label}' recorded: NPCs={snapshot.ActiveNPCs}, executable={snapshot.ExecutableNPCs}, projectiles={snapshot.ActiveProjectiles}, items={snapshot.ActiveItems}.";
	}

	internal static string Export()
	{
		if (!Enabled)
			return LastReportDirectory;

		Record("session", "manual-export");
		WriteSummary("active");
		return ReportDirectory;
	}

	internal static string Stop(string reason)
	{
		if (!Enabled)
		{
			ClearChallenge();
			return LastReportDirectory;
		}

		string completedDirectory = ReportDirectory;
		Record("session", "stopped", new { reason });
		WriteSummary(reason);
		TryLogInfo($"[GHR-TEST] Diagnostic session {SessionId} stopped ({reason}): {completedDirectory}");

		Enabled = false;
		SessionId = null;
		ReportDirectory = null;
		eventsPath = null;
		ClearChallenge();
		return completedDirectory;
	}

	internal static void ResetForWorldLoad()
	{
		if (Enabled)
			Stop("world-load-reset");
		ClearChallenge();
	}

	internal static void ResetAll()
	{
		if (Enabled)
			Stop("mod-unload");

		Enabled = false;
		SessionId = null;
		ReportDirectory = null;
		LastReportDirectory = null;
		eventsPath = null;
		eventCount = 0;
		lastIOError = null;
		ClearChallenge();
	}

	private static void WriteSummary(string state)
	{
		if (string.IsNullOrWhiteSpace(ReportDirectory))
			return;

		try
		{
			Player player = null;
			if (!Main.gameMenu && Main.myPlayer >= 0 && Main.myPlayer < Main.maxPlayers)
				player = Main.player[Main.myPlayer];

			var summary = new
			{
				schemaVersion = 1,
				sessionId = SessionId,
				state,
				startedUtc = startedUtc.ToString("O"),
				updatedUtc = DateTimeOffset.UtcNow.ToString("O"),
				eventCount,
				reportDirectory = ReportDirectory,
				lastIOError,
				environment = new
				{
					modName = ModInstance.Name,
					modVersion = ModInstance.Version.ToString(),
					tModLoader = BuildInfo.versionedNameDevFriendly,
					tModLoaderBuildIdentifier = BuildInfo.BuildIdentifier,
					terraria = Main.versionNumber,
					netMode = Main.netMode,
					worldName = Main.worldName ?? string.Empty
				},
				loadedMods = DiagnosticData.CaptureLoadedMods(),
				latestSnapshot = player == null ? null : DiagnosticData.CaptureSnapshot(player, "summary")
			};

			string json = JsonSerializer.Serialize(summary, IndentedJson);
			lock (FileLock)
				File.WriteAllText(Path.Combine(ReportDirectory, "summary.json"), json, Utf8NoBom);
		}
		catch (Exception exception)
		{
			lastIOError = $"{exception.GetType().FullName}: {exception.Message}";
			TryLogError("Could not write the diagnostic summary.", exception);
		}
	}

	private static void ClearChallenge()
	{
		enableChallenge = null;
		enableChallengeExpiresUtc = default;
	}

	private static void TryLogInfo(string message)
	{
		try
		{
			ModInstance.Logger.Info(message);
		}
		catch
		{
		}
	}

	private static void TryLogError(string message, Exception exception)
	{
		try
		{
			ModInstance.Logger.Error($"[GHR-TEST] {message}\n{exception}");
		}
		catch
		{
		}
	}
}

internal sealed class DiagnosticEventEntry
{
	public int SchemaVersion { get; init; }
	public string TimestampUtc { get; init; }
	public string SessionId { get; init; }
	public uint GameUpdateCount { get; init; }
	public string Category { get; init; }
	public string Outcome { get; init; }
	public object Data { get; init; }
}
