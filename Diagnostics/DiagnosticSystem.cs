using System;
using GodsHellfire_Reborn.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Diagnostics;

internal class DiagnosticSystem : ModSystem
{
	private static DefenseProbe pendingDefenseProbe;

	public override void OnWorldLoad()
	{
		pendingDefenseProbe = null;
		DiagnosticSession.ResetForWorldLoad();
	}

	/// <summary>
	/// Runs from the IL return barrier of SystemLoader.PostUpdateEverything,
	/// after every third-party ModSystem callback. A confirmed probe can therefore
	/// write and repair fatal fields without exposing that transient state to a
	/// later ModSystem in the same update group.
	/// </summary>
	internal static void RunPostUpdateEverythingBoundary()
	{
		DefenseProbe probe = pendingDefenseProbe;
		if (!DiagnosticSession.Enabled || probe == null || unchecked(Main.GameUpdateCount - probe.StartTick) < 1u)
		{
			AbsoluteGodSystem.RestoreProtectedPlayers();
			return;
		}

		if (!TryGetPlayer(probe.PlayerSlot, out Player player))
		{
			AbsoluteGodSystem.RestoreProtectedPlayers();
			DiagnosticSession.Record("defense-probe", "failed", new
			{
				reason = "The local player slot became unavailable.",
				probe.PlayerSlot
			});
			pendingDefenseProbe = null;
			Main.NewText("[GHR-TEST] Defense probe failed: local player unavailable.", Color.OrangeRed);
			return;
		}

		bool rawMutationApplied = false;
		string rawMutationException = null;
		try
		{
			// Deliberately bypass every normal hurt/death API. The update-boundary
			// barrier invokes this method after all ModSystem callbacks and repairs
			// them immediately below, with no third-party callback in between.
			player.creativeGodMode = false;
			player.immune = false;
			player.immuneTime = 0;
			player.active = false;
			player.dead = true;
			player.ghost = true;
			player.statLife = 0;
			player.respawnTimer = 600;
			rawMutationApplied = true;
		}
		catch (Exception exception)
		{
			rawMutationException = $"{exception.GetType().FullName}: {exception.Message}";
		}

		// Keep this call immediately adjacent to the raw writes. Reporting and chat
		// occur only after the protected player has been restored.
		AbsoluteGodSystem.RestoreProtectedPlayers();

		PlayerDiagnosticSnapshot afterBoundary = DiagnosticData.CapturePlayer(player);
		bool rawFieldRecoveryPassed = rawMutationApplied &&
			IsAlive(player) &&
			player.respawnTimer == 0 &&
			player.creativeGodMode &&
			player.immune &&
			AbsoluteGodPlayer.IsProtected(player);
		bool overallPassed = probe.HurtPassed && probe.KillMePassed && rawFieldRecoveryPassed;

		DiagnosticSession.Record("defense-probe", overallPassed ? "passed" : "failed", new
		{
			probe.HurtPassed,
			probe.KillMePassed,
			rawFieldRecoveryPassed,
			rawMutationApplied,
			rawMutationException,
			probe.HurtReturnedDamage,
			probe.HurtException,
			probe.KillMeException,
			before = probe.Before,
			afterImmediateEntryPoints = probe.AfterImmediateEntryPoints,
			afterBoundary
		});

		if (!rawFieldRecoveryPassed && AbsoluteGodPlayer.IsProtected(player))
			AbsoluteGodPlayer.Restore(player);

		pendingDefenseProbe = null;
		Main.NewText(
			overallPassed
				? "[GHR-TEST] Defense probe passed. Hurt, KillMe, and raw-field recovery were blocked/repaired."
				: "[GHR-TEST] Defense probe failed. Export the diagnostic report for review.",
			overallPassed ? Color.LightGreen : Color.OrangeRed);
	}

	public override void OnWorldUnload()
	{
		CancelPendingDefenseProbe("world-unload");
		DiagnosticSession.Stop("world-unload");
	}

	public override void Unload()
	{
		pendingDefenseProbe = null;
		DiagnosticSession.ResetAll();
	}

	internal static bool TryStartDefenseProbe(Player player, out string message)
	{
		if (!DiagnosticSession.Enabled)
		{
			message = "Enable diagnostics first.";
			return false;
		}

		if (Main.netMode != NetmodeID.SinglePlayer || Main.gameMenu || player == null || player.whoAmI != Main.myPlayer)
		{
			message = "The defense probe is restricted to the local player in single-player.";
			return false;
		}

		if (pendingDefenseProbe != null)
		{
			message = "A defense probe is already pending.";
			return false;
		}

		if (!AbsoluteGodPlayer.IsProtected(player))
		{
			message = "Equip Absolute_God in an active functional accessory slot before running the defense probe.";
			return false;
		}

		var probe = new DefenseProbe
		{
			PlayerSlot = player.whoAmI,
			StartTick = Main.GameUpdateCount,
			Before = DiagnosticData.CapturePlayer(player)
		};

		PlayerDeathReason reason = PlayerDeathReason.ByCustomReason(
			NetworkText.FromLiteral($"{player.name} failed a GodsHellfire_Reborn diagnostic probe."));

		try
		{
			// Disable vanilla's simple flags immediately before the call so this
			// exercise reaches the mod's protection detours/accessory check.
			player.creativeGodMode = false;
			player.immune = false;
			player.immuneTime = 0;
			probe.HurtReturnedDamage = player.Hurt(reason, int.MaxValue, 0, quiet: true, dodgeable: false);
			probe.HurtPassed = probe.HurtReturnedDamage == 0d && IsAlive(player);
		}
		catch (Exception exception)
		{
			probe.HurtException = $"{exception.GetType().FullName}: {exception.Message}";
			probe.HurtPassed = false;
		}

		if (!IsAlive(player))
			AbsoluteGodPlayer.Restore(player);

		try
		{
			player.creativeGodMode = false;
			player.KillMe(reason, double.MaxValue, 0, pvp: false);
			probe.KillMePassed = IsAlive(player);
		}
		catch (Exception exception)
		{
			probe.KillMeException = $"{exception.GetType().FullName}: {exception.Message}";
			probe.KillMePassed = false;
		}

		if (!IsAlive(player))
			AbsoluteGodPlayer.Restore(player);

		probe.AfterImmediateEntryPoints = DiagnosticData.CapturePlayer(player);
		pendingDefenseProbe = probe;

		DiagnosticSession.Record("defense-probe", "armed", new
		{
			probe.PlayerSlot,
			probe.StartTick,
			probe.HurtPassed,
			probe.KillMePassed,
			probe.HurtReturnedDamage,
			probe.HurtException,
			probe.KillMeException,
			before = probe.Before,
			afterImmediateEntryPoints = probe.AfterImmediateEntryPoints
		});

		message = "Defense probe armed. Keep the world running for one update tick; the outer IL barrier will write, repair, and verify fatal fields atomically.";
		return true;
	}

	internal static bool CancelPendingDefenseProbe(string reason)
	{
		if (pendingDefenseProbe == null)
			return false;

		DiagnosticSession.Record("defense-probe", "cancelled", new { reason });
		pendingDefenseProbe = null;
		return true;
	}

	internal static string GetPendingProbeStatus()
	{
		return pendingDefenseProbe == null
			? "none"
			: $"defense probe for player slot {pendingDefenseProbe.PlayerSlot}, started at tick {pendingDefenseProbe.StartTick}";
	}

	private static bool TryGetPlayer(int slot, out Player player)
	{
		if (slot >= 0 && slot < Main.maxPlayers)
		{
			player = Main.player[slot];
			return player != null;
		}

		player = null;
		return false;
	}

	private static bool IsAlive(Player player)
	{
		return player != null && player.active && !player.dead && !player.ghost && player.statLife > 0;
	}

	private sealed class DefenseProbe
	{
		internal int PlayerSlot;
		internal uint StartTick;
		internal bool HurtPassed;
		internal bool KillMePassed;
		internal double HurtReturnedDamage;
		internal string HurtException;
		internal string KillMeException;
		internal PlayerDiagnosticSnapshot Before;
		internal PlayerDiagnosticSnapshot AfterImmediateEntryPoints;
	}
}
