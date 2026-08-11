using System;
using System.Collections.Generic;
using System.Globalization;
using GodsHellfire_Reborn.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Diagnostics;

public sealed class GodsHellfireDiagnosticCommand : ModCommand
{
	private const string Confirmation = "confirm";
	private const float DefaultNpcRadius = 4000f;
	private const int NpcListLimit = 12;

	public override string Command => "ghrdiag";
	public override CommandType Type => CommandType.Chat;
	public override string Usage => "/ghrdiag help";
	public override string Description => "Single-player diagnostics for GodsHellfire_Reborn.";
	public override bool IsCaseSensitive => true;

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		string operation = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
		if (operation == "help")
		{
			ShowHelp(caller);
			return;
		}

		if (!IsLocalSinglePlayer(caller))
		{
			caller.Reply("[GHR-TEST] This command is restricted to the local player in a single-player world.", Color.OrangeRed);
			return;
		}

		switch (operation)
		{
			case "enable":
				Enable(caller, args);
				break;
			case "disable":
				Disable(caller);
				break;
			case "status":
				ShowStatus(caller);
				break;
			case "barriers":
				ShowBarriers(caller);
				break;
			case "snapshot":
				CaptureSnapshot(caller, args);
				break;
			case "mark":
				AddMarker(caller, args);
				break;
			case "npc":
				HandleNpc(caller, args);
				break;
			case "sweep":
				HandleSweep(caller, args);
				break;
			case "defense":
				RunDefenseProbe(caller, args);
				break;
			case "cancel":
				CancelProbe(caller);
				break;
			case "export":
				Export(caller);
				break;
			default:
				caller.Reply($"[GHR-TEST] Unknown operation '{args[0]}'. Use /ghrdiag help.", Color.OrangeRed);
				break;
		}
	}

	private static void ShowHelp(CommandCaller caller)
	{
		caller.Reply(
			"[GHR-TEST] GodsHellfire_Reborn diagnostics (single-player only)\n" +
			"/ghrdiag enable                         issue a 2-minute enable token\n" +
			"/ghrdiag enable <token>                 start a report session\n" +
			"/ghrdiag status | barriers              inspect session/player/IL state\n" +
			"/ghrdiag snapshot [label]               write a world snapshot\n" +
			"/ghrdiag mark <text>                    add a manual observation\n" +
			"/ghrdiag npc list [radius]              list up to 12 non-town NPCs\n" +
			"/ghrdiag npc execute <slot> <normal|force> confirm\n" +
			"/ghrdiag npc delete <slot> confirm      delete one logical NPC entity\n" +
			"/ghrdiag sweep <execute|delete> confirm run the production world sweep\n" +
			"/ghrdiag defense confirm                test Hurt, KillMe, and raw fields\n" +
			"/ghrdiag cancel | export | disable      manage the active session\n" +
			"Destructive probes never run automatically. Use a disposable test world.",
			Color.LightCyan);
	}

	private static void Enable(CommandCaller caller, string[] args)
	{
		if (DiagnosticSession.Enabled)
		{
			caller.Reply($"[GHR-TEST] Diagnostics are already enabled.\n{DiagnosticSession.ReportDirectory}", Color.LightGreen);
			return;
		}

		if (args.Length < 2)
		{
			string token = DiagnosticSession.IssueEnableChallenge();
			caller.Reply(
				$"[GHR-TEST] Diagnostics are disabled by default. To enable for this world/session, run:\n/ghrdiag enable {token}\nThe token expires in 2 minutes.",
				Color.Gold);
			return;
		}

		bool enabled = DiagnosticSession.TryStart(args[1], caller.Player, out string message);
		caller.Reply($"[GHR-TEST] {message}", enabled ? Color.LightGreen : Color.OrangeRed);
	}

	private static void Disable(CommandCaller caller)
	{
		if (!DiagnosticSession.Enabled)
		{
			caller.Reply("[GHR-TEST] Diagnostics are already disabled.", Color.Gray);
			return;
		}

		DiagnosticSystem.CancelPendingDefenseProbe("session-disabled");
		string directory = DiagnosticSession.Stop("manual-disable");
		caller.Reply($"[GHR-TEST] Diagnostics disabled. Completed report:\n{directory}", Color.LightGreen);
	}

	private static void ShowStatus(CommandCaller caller)
	{
		PlayerDiagnosticSnapshot player = DiagnosticData.CapturePlayer(caller.Player);
		List<BarrierDiagnosticEntry> barriers = DiagnosticData.CaptureBarriers();
		int installed = 0;
		foreach (BarrierDiagnosticEntry barrier in barriers)
		{
			if (barrier.Installed)
				installed++;
		}

		string report = DiagnosticSession.Enabled
			? DiagnosticSession.ReportDirectory
			: DiagnosticSession.LastReportDirectory ?? "none";
		caller.Reply(
			$"[GHR-TEST] enabled={DiagnosticSession.Enabled}, session={DiagnosticSession.SessionId ?? "none"}, events={DiagnosticSession.EventCount}\n" +
			$"player: active={player.Active}, dead={player.Dead}, ghost={player.Ghost}, life={player.Life}/{player.MaxLife}, protected={player.AbsoluteGodProtected}\n" +
			$"IL barriers: {installed}/{barriers.Count}; pending probe: {DiagnosticSystem.GetPendingProbeStatus()}\n" +
			$"report: {report}",
			DiagnosticSession.Enabled ? Color.LightGreen : Color.LightGray);

		if (DiagnosticSession.Enabled)
		{
			DiagnosticSession.Record("status", "queried", new
			{
				player,
				barriers,
				pendingProbe = DiagnosticSystem.GetPendingProbeStatus(),
				DiagnosticSession.LastIOError
			});
		}
	}

	private static void ShowBarriers(CommandCaller caller)
	{
		List<BarrierDiagnosticEntry> barriers = DiagnosticData.CaptureBarriers();
		if (barriers.Count == 0)
		{
			caller.Reply("[GHR-TEST] No IL barrier status records are available.", Color.OrangeRed);
			return;
		}

		int installed = 0;
		foreach (BarrierDiagnosticEntry barrier in barriers)
		{
			if (barrier.Installed)
				installed++;
			string detail = string.IsNullOrWhiteSpace(barrier.Detail) ? string.Empty : $" — {OneLine(barrier.Detail)}";
			caller.Reply($"[GHR-TEST] {(barrier.Installed ? "PASS" : "FAIL")} {barrier.Description}{detail}", barrier.Installed ? Color.LightGreen : Color.OrangeRed);
		}

		if (DiagnosticSession.Enabled)
			DiagnosticSession.Record("il-barriers", installed == barriers.Count ? "passed" : "failed", barriers);
	}

	private static void CaptureSnapshot(CommandCaller caller, string[] args)
	{
		if (!RequireSession(caller))
			return;

		string label = args.Length > 1 ? string.Join(' ', args, 1, args.Length - 1) : "manual";
		if (label.Length > 80)
			label = label[..80];

		caller.Reply($"[GHR-TEST] {DiagnosticSession.CaptureSnapshot(caller.Player, label)}", Color.LightGreen);
	}

	private static void AddMarker(CommandCaller caller, string[] args)
	{
		if (!RequireSession(caller))
			return;
		if (args.Length < 2)
		{
			caller.Reply("[GHR-TEST] Usage: /ghrdiag mark <text>", Color.OrangeRed);
			return;
		}

		string text = string.Join(' ', args, 1, args.Length - 1);
		if (text.Length > 512)
			text = text[..512];

		DiagnosticSession.Record("marker", "recorded", new { text });
		caller.Reply("[GHR-TEST] Marker recorded.", Color.LightGreen);
	}

	private static void HandleNpc(CommandCaller caller, string[] args)
	{
		if (!RequireSession(caller))
			return;
		if (args.Length < 2)
		{
			caller.Reply("[GHR-TEST] Usage: /ghrdiag npc <list|execute|delete> ...", Color.OrangeRed);
			return;
		}

		switch (args[1].ToLowerInvariant())
		{
			case "list":
				ListNpcs(caller, args);
				break;
			case "execute":
				ExecuteNpc(caller, args);
				break;
			case "delete":
				DeleteNpc(caller, args);
				break;
			default:
				caller.Reply("[GHR-TEST] Usage: /ghrdiag npc <list|execute|delete> ...", Color.OrangeRed);
				break;
		}
	}

	private static void ListNpcs(CommandCaller caller, string[] args)
	{
		float radius = DefaultNpcRadius;
		if (args.Length >= 3 && (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || radius < 1f || radius > 30000f))
		{
			caller.Reply("[GHR-TEST] Radius must be between 1 and 30000 pixels.", Color.OrangeRed);
			return;
		}

		List<NpcDiagnosticEntry> entries = DiagnosticData.FindNearestExecutableNPCs(caller.Player, radius, NpcListLimit);
		caller.Reply($"[GHR-TEST] Nearest non-town NPCs within {radius:0} px: {entries.Count}", Color.LightCyan);
		foreach (NpcDiagnosticEntry entry in entries)
		{
			caller.Reply(
				$"#{entry.Slot} {OneLine(entry.FullName)} [{entry.ModName}] life={entry.Life}/{entry.MaxLife} active={entry.Active} invulnerable={entry.DontTakeDamage || entry.Immortal} verdict={entry.Verdict} gen={entry.SpawnGeneration}",
				Color.White);
		}

		DiagnosticSession.Record("npc-list", "captured", new { radius, entries });
	}

	private static void ExecuteNpc(CommandCaller caller, string[] args)
	{
		NPC target = null;
		string error = null;
		if (args.Length < 5 || !TryGetTarget(args[2], out target, out error))
		{
			caller.Reply($"[GHR-TEST] {error ?? "Usage: /ghrdiag npc execute <slot> <normal|force> confirm"}", Color.OrangeRed);
			return;
		}

		string mode = args[3].ToLowerInvariant();
		if ((mode != "normal" && mode != "force") || !IsConfirmation(args[4]))
		{
			caller.Reply("[GHR-TEST] Usage: /ghrdiag npc execute <slot> <normal|force> confirm", Color.OrangeRed);
			return;
		}

		NpcDiagnosticEntry before = DiagnosticData.CaptureNpc(target);
		try
		{
			NPCExecution.Execute(target, forceParent: mode == "force");
			NpcDiagnosticEntry after = DiagnosticData.CaptureNpc(target);
			DiagnosticSession.Record("npc-execute", "invoked", new { mode, before, after });
			caller.Reply($"[GHR-TEST] {mode} execution invoked for slot {before.Slot}; active after immediate pass={after.Active}.", Color.LightGreen);
		}
		catch (Exception exception)
		{
			DiagnosticSession.Record("npc-execute", "exception", new { mode, before, exception = exception.ToString() });
			caller.Reply($"[GHR-TEST] Execution threw {exception.GetType().Name}; export the report.", Color.OrangeRed);
		}
	}

	private static void DeleteNpc(CommandCaller caller, string[] args)
	{
		NPC target = null;
		string error = null;
		if (args.Length < 4 || !TryGetTarget(args[2], out target, out error) || !IsConfirmation(args[3]))
		{
			caller.Reply($"[GHR-TEST] {error ?? "Usage: /ghrdiag npc delete <slot> confirm"}", Color.OrangeRed);
			return;
		}

		NpcDiagnosticEntry before = DiagnosticData.CaptureNpc(target);
		try
		{
			NPCExecution.Delete(target, forceParent: true);
			NpcDiagnosticEntry after = DiagnosticData.CaptureNpc(target);
			DiagnosticSession.Record("npc-delete", "invoked", new { before, after });
			caller.Reply($"[GHR-TEST] Deletion invoked for logical NPC at slot {before.Slot}; active after immediate pass={after.Active}.", Color.LightGreen);
		}
		catch (Exception exception)
		{
			DiagnosticSession.Record("npc-delete", "exception", new { before, exception = exception.ToString() });
			caller.Reply($"[GHR-TEST] Deletion threw {exception.GetType().Name}; export the report.", Color.OrangeRed);
		}
	}

	private static void HandleSweep(CommandCaller caller, string[] args)
	{
		if (!RequireSession(caller))
			return;
		if (args.Length < 3 || !IsConfirmation(args[2]))
		{
			caller.Reply("[GHR-TEST] Usage: /ghrdiag sweep <execute|delete> confirm", Color.OrangeRed);
			return;
		}

		string mode = args[1].ToLowerInvariant();
		if (mode != "execute" && mode != "delete")
		{
			caller.Reply("[GHR-TEST] Sweep mode must be execute or delete.", Color.OrangeRed);
			return;
		}

		DiagnosticSnapshot before = DiagnosticData.CaptureSnapshot(caller.Player, $"before-{mode}-sweep");
		try
		{
			if (mode == "delete")
				NPCExecution.BeginDeletionSweep();
			else
				NPCExecution.BeginExecutionSweep();

			DiagnosticSnapshot after = DiagnosticData.CaptureSnapshot(caller.Player, $"after-{mode}-sweep");
			DiagnosticSession.Record("world-sweep", "invoked", new { mode, before, after });
			caller.Reply(
				$"[GHR-TEST] {mode} sweep invoked. Executable NPCs {before.ExecutableNPCs}->{after.ExecutableNPCs}; projectiles {before.ActiveProjectiles}->{after.ActiveProjectiles}; dropped items remain {after.ActiveItems}.",
				Color.LightGreen);
		}
		catch (Exception exception)
		{
			DiagnosticSession.Record("world-sweep", "exception", new { mode, before, exception = exception.ToString() });
			caller.Reply($"[GHR-TEST] Sweep threw {exception.GetType().Name}; export the report.", Color.OrangeRed);
		}
	}

	private static void RunDefenseProbe(CommandCaller caller, string[] args)
	{
		if (!RequireSession(caller))
			return;
		if (args.Length < 2 || !IsConfirmation(args[1]))
		{
			caller.Reply("[GHR-TEST] Usage: /ghrdiag defense confirm. Equip Absolute_God and use a disposable test world.", Color.Gold);
			return;
		}

		bool started = DiagnosticSystem.TryStartDefenseProbe(caller.Player, out string message);
		caller.Reply($"[GHR-TEST] {message}", started ? Color.LightGreen : Color.OrangeRed);
	}

	private static void CancelProbe(CommandCaller caller)
	{
		if (!RequireSession(caller))
			return;

		bool cancelled = DiagnosticSystem.CancelPendingDefenseProbe("manual-cancel");
		caller.Reply(cancelled ? "[GHR-TEST] Pending probe cancelled." : "[GHR-TEST] No probe is pending.", cancelled ? Color.LightGreen : Color.Gray);
	}

	private static void Export(CommandCaller caller)
	{
		if (!RequireSession(caller))
			return;

		string directory = DiagnosticSession.Export();
		caller.Reply($"[GHR-TEST] Report flushed:\n{directory}", Color.LightGreen);
	}

	private static bool RequireSession(CommandCaller caller)
	{
		if (DiagnosticSession.Enabled)
			return true;

		caller.Reply("[GHR-TEST] Diagnostics are disabled. Run /ghrdiag enable to request a token.", Color.Gold);
		return false;
	}

	private static bool TryGetTarget(string text, out NPC target, out string error)
	{
		target = null;
		error = null;
		if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) || slot < 0 || slot >= Main.maxNPCs)
		{
			error = $"NPC slot must be between 0 and {Main.maxNPCs - 1}.";
			return false;
		}

		NPC candidate = Main.npc[slot];
		if (!NPCExecution.CanExecute(candidate))
		{
			error = $"NPC slot {slot} is inactive or is a town NPC.";
			return false;
		}

		target = candidate;
		return true;
	}

	private static bool IsConfirmation(string text)
	{
		return string.Equals(text, Confirmation, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsLocalSinglePlayer(CommandCaller caller)
	{
		return Main.netMode == NetmodeID.SinglePlayer &&
			!Main.gameMenu &&
			caller.Player != null &&
			caller.Player.whoAmI == Main.myPlayer;
	}

	private static string OneLine(string text)
	{
		return string.IsNullOrWhiteSpace(text)
			? string.Empty
			: text.Replace('\r', ' ').Replace('\n', ' ');
	}
}
