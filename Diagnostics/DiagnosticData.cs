using System;
using System.Collections.Generic;
using GodsHellfire_Reborn.Systems;
using Terraria;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Diagnostics;

internal static class DiagnosticData
{
	internal static DiagnosticSnapshot CaptureSnapshot(Player player, string label)
	{
		int activeNPCs = 0;
		int executableNPCs = 0;
		int townNPCs = 0;
		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (npc == null || !npc.active)
				continue;

			activeNPCs++;
			if (npc.isLikeATownNPC)
				townNPCs++;
			else
				executableNPCs++;
		}

		int activeProjectiles = 0;
		int hostileProjectiles = 0;
		int friendlyProjectiles = 0;
		for (int i = 0; i < Main.maxProjectiles; i++)
		{
			Projectile projectile = Main.projectile[i];
			if (projectile == null || !projectile.active)
				continue;

			activeProjectiles++;
			if (projectile.hostile)
				hostileProjectiles++;
			if (projectile.friendly)
				friendlyProjectiles++;
		}

		int activeItems = 0;
		for (int i = 0; i < Main.maxItems; i++)
		{
			Item item = Main.item[i];
			if (item != null && item.active)
				activeItems++;
		}

		var execution = NPCExecution.GetDiagnosticState();
		return new DiagnosticSnapshot
		{
			Label = label,
			GameUpdateCount = Main.GameUpdateCount,
			WorldName = Main.worldName ?? string.Empty,
			NetMode = Main.netMode,
			Player = CapturePlayer(player),
			ActiveNPCs = activeNPCs,
			ExecutableNPCs = executableNPCs,
			TownNPCs = townNPCs,
			ActiveProjectiles = activeProjectiles,
			HostileProjectiles = hostileProjectiles,
			FriendlyProjectiles = friendlyProjectiles,
			ActiveItems = activeItems,
			Execution = new ExecutionDiagnosticSnapshot
			{
				WorldSweep = execution.WorldSweep,
				SweepTicksRemaining = execution.SweepTicksRemaining,
				PendingExecutions = execution.PendingExecutions,
				PendingDeletions = execution.PendingDeletions,
				ActiveExecutionDepth = execution.ActiveExecutionDepth
			},
			Barriers = CaptureBarriers()
		};
	}

	internal static PlayerDiagnosticSnapshot CapturePlayer(Player player)
	{
		if (player == null)
			return null;

		return new PlayerDiagnosticSnapshot
		{
			Slot = player.whoAmI,
			Name = player.name ?? string.Empty,
			Active = player.active,
			Dead = player.dead,
			Ghost = player.ghost,
			Life = player.statLife,
			MaxLife = player.statLifeMax2,
			Mana = player.statMana,
			MaxMana = player.statManaMax2,
			Immune = player.immune,
			ImmuneTime = player.immuneTime,
			CreativeGodMode = player.creativeGodMode,
			AbsoluteGodProtected = AbsoluteGodPlayer.IsProtected(player),
			PositionX = player.position.X,
			PositionY = player.position.Y
		};
	}

	internal static List<NpcDiagnosticEntry> FindNearestExecutableNPCs(Player player, float radius, int limit)
	{
		float radiusSquared = radius * radius;
		var entries = new List<NpcDiagnosticEntry>();

		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (!NPCExecution.CanExecute(npc))
				continue;

			float distanceSquared = player == null
				? 0f
				: (npc.Center - player.Center).LengthSquared();
			if (player != null && distanceSquared > radiusSquared)
				continue;

			entries.Add(CaptureNpc(npc, MathF.Sqrt(distanceSquared)));
		}

		entries.Sort((left, right) => left.DistancePixels.CompareTo(right.DistancePixels));
		if (entries.Count > limit)
			entries.RemoveRange(limit, entries.Count - limit);

		return entries;
	}

	internal static NpcDiagnosticEntry CaptureNpc(NPC npc, float? distancePixels = null)
	{
		if (npc == null)
			return null;

		var verdict = NPCExecution.GetDiagnosticState(npc);
		string fullName;
		string modName;
		try
		{
			fullName = npc.FullName ?? $"NPC type {npc.type}";
			modName = npc.ModNPC?.Mod?.Name ?? "Terraria";
		}
		catch (Exception)
		{
			// A diagnostic snapshot must remain available even if a third-party
			// display-name hook throws while its NPC is in a transitional state.
			fullName = $"NPC type {npc.type}";
			modName = npc.ModNPC?.Mod?.Name ?? "Unknown";
		}

		return new NpcDiagnosticEntry
		{
			Slot = npc.whoAmI,
			Type = npc.type,
			FullName = fullName,
			ModName = modName,
			Active = npc.active,
			Life = npc.life,
			MaxLife = npc.lifeMax,
			Boss = npc.boss,
			Friendly = npc.friendly,
			TownNPC = npc.isLikeATownNPC,
			DontTakeDamage = npc.dontTakeDamage,
			Immortal = npc.immortal,
			RealLife = npc.realLife,
			DistancePixels = distancePixels ?? 0f,
			Verdict = verdict.Verdict,
			SpawnGeneration = verdict.SpawnGeneration,
			VerdictGeneration = verdict.VerdictGeneration
		};
	}

	internal static List<BarrierDiagnosticEntry> CaptureBarriers()
	{
		ILBarrierDiagnosticStatus[] statuses = ExtremeILSystem.GetBarrierStatuses();
		var result = new List<BarrierDiagnosticEntry>(statuses.Length);
		foreach (ILBarrierDiagnosticStatus status in statuses)
		{
			result.Add(new BarrierDiagnosticEntry
			{
				Description = status.Description,
				Installed = status.Installed,
				Detail = status.Detail
			});
		}

		return result;
	}

	internal static List<LoadedModDiagnosticEntry> CaptureLoadedMods()
	{
		var result = new List<LoadedModDiagnosticEntry>();
		foreach (Mod mod in Terraria.ModLoader.ModLoader.Mods)
		{
			result.Add(new LoadedModDiagnosticEntry
			{
				Name = mod.Name,
				DisplayName = mod.DisplayName,
				Version = mod.Version.ToString()
			});
		}

		return result;
	}
}

internal sealed class DiagnosticSnapshot
{
	public string Label { get; init; }
	public uint GameUpdateCount { get; init; }
	public string WorldName { get; init; }
	public int NetMode { get; init; }
	public PlayerDiagnosticSnapshot Player { get; init; }
	public int ActiveNPCs { get; init; }
	public int ExecutableNPCs { get; init; }
	public int TownNPCs { get; init; }
	public int ActiveProjectiles { get; init; }
	public int HostileProjectiles { get; init; }
	public int FriendlyProjectiles { get; init; }
	public int ActiveItems { get; init; }
	public ExecutionDiagnosticSnapshot Execution { get; init; }
	public List<BarrierDiagnosticEntry> Barriers { get; init; }
}

internal sealed class PlayerDiagnosticSnapshot
{
	public int Slot { get; init; }
	public string Name { get; init; }
	public bool Active { get; init; }
	public bool Dead { get; init; }
	public bool Ghost { get; init; }
	public int Life { get; init; }
	public int MaxLife { get; init; }
	public int Mana { get; init; }
	public int MaxMana { get; init; }
	public bool Immune { get; init; }
	public int ImmuneTime { get; init; }
	public bool CreativeGodMode { get; init; }
	public bool AbsoluteGodProtected { get; init; }
	public float PositionX { get; init; }
	public float PositionY { get; init; }
}

internal sealed class NpcDiagnosticEntry
{
	public int Slot { get; init; }
	public int Type { get; init; }
	public string FullName { get; init; }
	public string ModName { get; init; }
	public bool Active { get; init; }
	public int Life { get; init; }
	public int MaxLife { get; init; }
	public bool Boss { get; init; }
	public bool Friendly { get; init; }
	public bool TownNPC { get; init; }
	public bool DontTakeDamage { get; init; }
	public bool Immortal { get; init; }
	public int RealLife { get; init; }
	public float DistancePixels { get; init; }
	public string Verdict { get; init; }
	public int SpawnGeneration { get; init; }
	public int VerdictGeneration { get; init; }
}

internal sealed class ExecutionDiagnosticSnapshot
{
	public string WorldSweep { get; init; }
	public int SweepTicksRemaining { get; init; }
	public int PendingExecutions { get; init; }
	public int PendingDeletions { get; init; }
	public int ActiveExecutionDepth { get; init; }
}

internal sealed class BarrierDiagnosticEntry
{
	public string Description { get; init; }
	public bool Installed { get; init; }
	public string Detail { get; init; }
}

internal sealed class LoadedModDiagnosticEntry
{
	public string Name { get; init; }
	public string DisplayName { get; init; }
	public string Version { get; init; }
}
