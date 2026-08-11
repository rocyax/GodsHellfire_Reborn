using System;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Systems;

internal static class NPCExecution
{
	private enum WorldSweepMode : byte
	{
		None,
		Execution,
		Deletion
	}

	private enum PersistentVerdict : byte
	{
		None,
		Execution,
		Deletion
	}

	// The immediate loop catches phases spawned synchronously by death/loot
	// hooks. The short cross-tick sweep catches transformations deferred by a
	// protected boss until a later update without permanently disabling spawns.
	private const int ImmediateSweepPassLimit = 100;
	private const int WorldSweepDurationTicks = 120;

	private static readonly HashSet<int> PendingExecutions = new();
	private static readonly HashSet<int> PendingDeletions = new();
	// GlobalNPC instances and their fields can be reset by NPC.SetDefaults during
	// an in-place phase transition. Keep the authoritative verdict outside the
	// NPC, keyed by its array slot and actual OnSpawn generation. A genuine new
	// spawn changes generation and cannot inherit a stale verdict by slot reuse.
	private static readonly int[] SpawnGenerations = new int[Main.maxNPCs];
	private static readonly int[] VerdictGenerations = new int[Main.maxNPCs];
	private static readonly PersistentVerdict[] PersistentVerdicts = new PersistentVerdict[Main.maxNPCs];
	[ThreadStatic]
	private static Stack<NPC> activeExecutionTargets;
	private static WorldSweepMode activeWorldSweep;
	private static int worldSweepTicksRemaining;

	internal static bool CanExecute(NPC npc)
	{
		return npc != null && npc.active && !npc.isLikeATownNPC;
	}

	private static bool TryGetSlot(NPC npc, out int index)
	{
		index = npc?.whoAmI ?? -1;
		return index >= 0 && index < SpawnGenerations.Length;
	}

	private static PersistentVerdict GetPersistentVerdict(NPC npc)
	{
		if (!TryGetSlot(npc, out int index))
			return PersistentVerdict.None;

		int generation = SpawnGenerations[index];
		return generation != 0 && VerdictGenerations[index] == generation
			? PersistentVerdicts[index]
			: PersistentVerdict.None;
	}

	private static PersistentVerdict GetEffectiveVerdict(NPC npc)
	{
		PersistentVerdict verdict = GetPersistentVerdict(npc);
		if (verdict != PersistentVerdict.None || npc == null)
			return verdict;

		// Compatibility fallback for a verdict written before the generation
		// record was established. Every enforcement path below promotes it into
		// the persistent table.
		ExecutionGlobalNPC state = npc.GetGlobalNPC<ExecutionGlobalNPC>();
		if (state.Erased)
			return PersistentVerdict.Deletion;
		if (state.Condemned)
			return PersistentVerdict.Execution;

		return PersistentVerdict.None;
	}

	private static void SetPersistentVerdict(NPC npc, PersistentVerdict requestedVerdict)
	{
		if (!TryGetSlot(npc, out int index))
			return;

		int generation = SpawnGenerations[index];
		if (generation == 0)
		{
			generation = 1;
			SpawnGenerations[index] = generation;
		}

		// Deletion is intentionally stronger than execution and cannot be
		// downgraded if an erased NPC is briefly reactivated and encountered by a
		// later execution path.
		PersistentVerdict currentVerdict = GetPersistentVerdict(npc);
		PersistentVerdict verdict = currentVerdict == PersistentVerdict.Deletion
			? PersistentVerdict.Deletion
			: requestedVerdict;

		PersistentVerdicts[index] = verdict;
		VerdictGenerations[index] = generation;

		ExecutionGlobalNPC state = npc.GetGlobalNPC<ExecutionGlobalNPC>();
		state.Erased = verdict == PersistentVerdict.Deletion;
		state.Condemned = verdict == PersistentVerdict.Execution;
	}

	private static void BeginSpawnGeneration(NPC npc)
	{
		if (!TryGetSlot(npc, out int index))
			return;

		int nextGeneration = unchecked(SpawnGenerations[index] + 1);
		if (nextGeneration <= 0)
			nextGeneration = 1;

		SpawnGenerations[index] = nextGeneration;
		VerdictGenerations[index] = 0;
		PersistentVerdicts[index] = PersistentVerdict.None;
		PendingExecutions.Remove(index);
		PendingDeletions.Remove(index);
	}

	internal static void RegisterSpawn(NPC npc, IEntitySource source, ExecutionGlobalNPC state)
	{
		// Resolve inheritance before changing the generation. This also handles a
		// source whose parent is the same NPC object during an in-place respawn.
		PersistentVerdict inheritedVerdict = PersistentVerdict.None;
		if (source is EntitySource_Parent parentSource && parentSource.Entity is NPC parentNPC)
			inheritedVerdict = GetEffectiveVerdict(parentNPC);

		BeginSpawnGeneration(npc);
		state.Condemned = false;
		state.Erased = false;

		if (!CanExecute(npc))
			return;

		if (inheritedVerdict == PersistentVerdict.Deletion)
			QueueDeletedDescendant(npc);
		else if (inheritedVerdict == PersistentVerdict.Execution)
			QueueDescendant(npc);
	}

	internal static void BeginExecutionSweep()
	{
		BeginWorldSweep(WorldSweepMode.Execution);
	}

	internal static void BeginDeletionSweep()
	{
		BeginWorldSweep(WorldSweepMode.Deletion);
	}

	private static void BeginWorldSweep(WorldSweepMode mode)
	{
		activeWorldSweep = mode;
		worldSweepTicksRemaining = WorldSweepDurationTicks;

		// Sweep repeatedly in the initiating frame. An NPC death can place a
		// replacement phase into an array slot that this pass already visited.
		for (int pass = 0; pass < ImmediateSweepPassLimit; pass++)
		{
			bool foundEntity = SweepActiveNPCs(mode);
			if (mode == WorldSweepMode.Deletion)
				foundEntity |= DeleteActiveProjectiles();

			if (!foundEntity)
				break;
		}
	}

	internal static void Execute(NPC hitTarget)
	{
		if (!CanExecute(hitTarget))
			return;

		if (GetEffectiveVerdict(hitTarget) == PersistentVerdict.Deletion)
		{
			ForceDelete(hitTarget);
			return;
		}

		NPC primary = ResolvePrimaryTarget(hitTarget);
		if (!CanExecute(primary))
			return;

		if (GetEffectiveVerdict(primary) == PersistentVerdict.Deletion)
		{
			ForceDelete(primary);
			return;
		}

		List<NPC> relatedParts = SnapshotRelatedParts(primary, hitTarget);
		ExecuteSingle(primary);

		// Segments linked by Terraria's realLife field are one logical entity. They
		// are deactivated without their own loot pass after the primary has died.
		// The snapshot holds NPC references rather than slot indices: executing the
		// primary can free slots, and a replacement phase spawned by a death hook
		// may occupy one of them before this loop runs. A reference cannot be
		// redirected onto that unrelated new entity by slot reuse.
		foreach (NPC part in relatedParts)
		{
			if (ReferenceEquals(part, primary) || !CanExecute(part))
				continue;

			SetPersistentVerdict(part, PersistentVerdict.Execution);
			ForceDeactivate(part);
		}
	}

	/// <summary>
	/// Diagnostic single-target counterpart to the deletion sweep. This uses the
	/// same persistent deletion verdict and vanilla realLife linkage as the
	/// production execution path, but deliberately invokes no death or loot hook.
	/// </summary>
	internal static void Delete(NPC hitTarget)
	{
		if (!CanExecute(hitTarget))
			return;

		NPC primary = ResolvePrimaryTarget(hitTarget);
		if (!CanExecute(primary))
			return;

		List<NPC> relatedParts = SnapshotRelatedParts(primary, hitTarget);
		ForceDelete(primary);

		foreach (NPC part in relatedParts)
		{
			if (!ReferenceEquals(part, primary) && CanExecute(part))
				ForceDelete(part);
		}
	}

	private static NPC ResolvePrimaryTarget(NPC hitTarget)
	{
		if (hitTarget.realLife >= 0 && hitTarget.realLife < Main.maxNPCs)
		{
			NPC realLife = Main.npc[hitTarget.realLife];
			if (CanExecute(realLife))
				return realLife;
		}

		return hitTarget;
	}

	private static List<NPC> SnapshotRelatedParts(NPC primary, NPC hitTarget)
	{
		var result = new List<NPC>();

		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (npc.active && (npc.whoAmI == primary.whoAmI || npc.realLife == primary.whoAmI))
				result.Add(npc);
		}

		if (!result.Contains(hitTarget))
			result.Add(hitTarget);

		return result;
	}

	private static void ExecuteSingle(NPC npc)
	{
		if (!CanExecute(npc))
			return;

		PushExecutionTarget(npc);
		try
		{
			SetPersistentVerdict(npc, PersistentVerdict.Execution);
			npc.dontTakeDamage = false;
			npc.immortal = false;
			npc.chaseable = true;

			// First allow Terraria/tModLoader to perform a normal instant death so
			// loot, bestiary and progression hooks run in their expected order. The
			// IL return barriers force only this target through CheckDead/PreKill.
			npc.StrikeInstantKill();
			if (!npc.active)
				return;

			// A phase hook kept the same NPC alive or transformed it in place.
			// Re-condemn it and finish through the normal loot path before the final
			// direct deactivation fallback.
			SetPersistentVerdict(npc, PersistentVerdict.Execution);
			npc.dontTakeDamage = false;
			npc.immortal = false;
			npc.life = 0;
			npc.NPCLoot();
			SetPersistentVerdict(npc, PersistentVerdict.Execution);
			ForceDeactivate(npc);
		}
		finally
		{
			PopExecutionTarget(npc);
		}
	}

	internal static bool OverrideDeathGate(bool originalResult, NPC npc)
	{
		return IsActiveExecutionTarget(npc) || originalResult;
	}

	private static bool IsActiveExecutionTarget(NPC npc)
	{
		Stack<NPC> targets = activeExecutionTargets;
		if (npc == null || targets == null || targets.Count == 0)
			return false;

		foreach (NPC target in targets)
		{
			if (ReferenceEquals(target, npc))
				return true;
		}

		return false;
	}

	private static void PushExecutionTarget(NPC npc)
	{
		(activeExecutionTargets ??= new Stack<NPC>()).Push(npc);
	}

	private static void PopExecutionTarget(NPC npc)
	{
		Stack<NPC> targets = activeExecutionTargets;
		if (targets == null || targets.Count == 0)
			return;

		if (ReferenceEquals(targets.Peek(), npc))
		{
			targets.Pop();
			return;
		}

		// Defensive recovery for an unexpected nested exception. Execution is
		// main-thread-only in single player, so clearing is safer than allowing a
		// stale target to affect an unrelated death later.
		targets.Clear();
	}

	private static void ForceDeactivate(NPC npc)
	{
		if (npc == null || !npc.active || npc.isLikeATownNPC)
			return;

		if (GetEffectiveVerdict(npc) == PersistentVerdict.Deletion)
		{
			ForceDelete(npc);
			return;
		}

		SetPersistentVerdict(npc, PersistentVerdict.Execution);
		npc.life = 0;
		npc.dontTakeDamage = false;
		npc.immortal = false;
		npc.active = false;
		npc.netUpdate = true;

		if (Main.netMode == NetmodeID.Server)
			NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, npc.whoAmI);
	}

	private static void ForceDelete(NPC npc)
	{
		if (!CanExecute(npc))
			return;

		SetPersistentVerdict(npc, PersistentVerdict.Deletion);
		PendingExecutions.Remove(npc.whoAmI);

		// This is deliberate entity removal, not a kill. Do not call HitEffect,
		// CheckDead, StrikeInstantKill, NPCLoot, or any other death hook. Keep the
		// previous life value as well, so external phase code cannot mistake this
		// direct active=false removal for a life <= 0 death.
		npc.active = false;
		npc.damage = 0;
		npc.netUpdate = true;

		if (Main.netMode == NetmodeID.Server)
			NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, npc.whoAmI);
	}

	private static bool DeleteActiveProjectiles()
	{
		bool foundProjectile = false;

		for (int i = 0; i < Main.maxProjectiles; i++)
		{
			Projectile projectile = Main.projectile[i];
			if (!projectile.active)
				continue;

			foundProjectile = true;

			// Directly deactivate instead of calling Kill(). Kill/OnKill hooks can
			// spawn replacement projectiles, gore, dust, or other entities.
			projectile.active = false;
			projectile.timeLeft = 0;
			projectile.damage = 0;
			projectile.hostile = false;
			projectile.friendly = false;
		}

		return foundProjectile;
	}

	private static bool SweepActiveNPCs(WorldSweepMode mode)
	{
		bool foundNPC = false;

		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (!CanExecute(npc))
				continue;

			foundNPC = true;
			if (mode == WorldSweepMode.Deletion)
				ForceDelete(npc);
			else
				Execute(npc);
		}

		return foundNPC;
	}

	internal static void QueueDescendant(NPC npc)
	{
		if (!CanExecute(npc))
			return;

		if (GetEffectiveVerdict(npc) == PersistentVerdict.Deletion)
		{
			QueueDeletedDescendant(npc);
			return;
		}

		SetPersistentVerdict(npc, PersistentVerdict.Execution);
		PendingExecutions.Add(npc.whoAmI);
	}

	internal static void QueueDeletedDescendant(NPC npc)
	{
		if (!CanExecute(npc))
			return;

		SetPersistentVerdict(npc, PersistentVerdict.Deletion);
		PendingExecutions.Remove(npc.whoAmI);
		PendingDeletions.Add(npc.whoAmI);
	}

	internal static void DrainPendingExecutions()
	{
		// A phase can synchronously spawn another phase. Keep draining additions
		// made during execution, with a per-tick guard against malicious loops.
		for (int generation = 0; generation < 100 && PendingExecutions.Count > 0; generation++)
		{
			int[] pending = new int[PendingExecutions.Count];
			PendingExecutions.CopyTo(pending);
			PendingExecutions.Clear();

			foreach (int index in pending)
			{
				if (index < 0 || index >= Main.maxNPCs)
					continue;

				NPC npc = Main.npc[index];
				PersistentVerdict verdict = GetEffectiveVerdict(npc);
				if (verdict == PersistentVerdict.Deletion)
					ForceDelete(npc);
				else if (verdict == PersistentVerdict.Execution)
					Execute(npc);
			}
		}
	}

	internal static void DrainPendingDeletions()
	{
		for (int generation = 0; generation < 100 && PendingDeletions.Count > 0; generation++)
		{
			int[] pending = new int[PendingDeletions.Count];
			PendingDeletions.CopyTo(pending);
			PendingDeletions.Clear();

			foreach (int index in pending)
			{
				if (index < 0 || index >= Main.maxNPCs)
					continue;

				NPC npc = Main.npc[index];
				if (GetEffectiveVerdict(npc) == PersistentVerdict.Deletion)
					ForceDelete(npc);
			}
		}
	}

	internal static void RunWorldNPCSweep()
	{
		if (activeWorldSweep != WorldSweepMode.None)
			SweepActiveNPCs(activeWorldSweep);
	}

	internal static void RunWorldProjectileSweep()
	{
		if (activeWorldSweep == WorldSweepMode.Deletion)
			DeleteActiveProjectiles();
	}

	internal static void AdvanceWorldSweep()
	{
		if (activeWorldSweep == WorldSweepMode.None)
			return;

		// This final pass catches entities created after their normal update
		// groups, such as a projectile spawned from another system hook.
		RunWorldNPCSweep();
		RunWorldProjectileSweep();

		worldSweepTicksRemaining--;
		if (worldSweepTicksRemaining <= 0)
		{
			activeWorldSweep = WorldSweepMode.None;
			worldSweepTicksRemaining = 0;
		}
	}

	internal static void ResetState()
	{
		PendingExecutions.Clear();
		PendingDeletions.Clear();
		activeExecutionTargets?.Clear();
		Array.Clear(SpawnGenerations, 0, SpawnGenerations.Length);
		Array.Clear(VerdictGenerations, 0, VerdictGenerations.Length);
		Array.Clear(PersistentVerdicts, 0, PersistentVerdicts.Length);
		activeWorldSweep = WorldSweepMode.None;
		worldSweepTicksRemaining = 0;
	}

	internal static (string WorldSweep, int SweepTicksRemaining, int PendingExecutions, int PendingDeletions, int ActiveExecutionDepth) GetDiagnosticState()
	{
		return (
			activeWorldSweep.ToString(),
			worldSweepTicksRemaining,
			PendingExecutions.Count,
			PendingDeletions.Count,
			activeExecutionTargets?.Count ?? 0);
	}

	internal static (string Verdict, int SpawnGeneration, int VerdictGeneration) GetDiagnosticState(NPC npc)
	{
		if (!TryGetSlot(npc, out int index))
			return (PersistentVerdict.None.ToString(), 0, 0);

		return (
			GetEffectiveVerdict(npc).ToString(),
			SpawnGenerations[index],
			VerdictGenerations[index]);
	}

	internal static void SuppressReactivatedNPCs()
	{
		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (!npc.active)
				continue;

			PersistentVerdict verdict = GetEffectiveVerdict(npc);
			if (verdict == PersistentVerdict.Deletion)
				ForceDelete(npc);
			else if (verdict == PersistentVerdict.Execution)
				ForceDeactivate(npc);
		}
	}
}

internal class ExecutionGlobalNPC : GlobalNPC
{
	public override bool InstancePerEntity => true;

	internal bool Condemned;
	internal bool Erased;

	public override void SetDefaults(NPC entity)
	{
		Condemned = false;
		Erased = false;
	}

	public override void OnSpawn(NPC npc, IEntitySource source)
	{
		NPCExecution.RegisterSpawn(npc, source, this);
	}
}

internal class ExecutionSystem : ModSystem
{
	public override void OnWorldLoad()
	{
		NPCExecution.ResetState();
	}

	public override void PreUpdateNPCs()
	{
		NPCExecution.SuppressReactivatedNPCs();
		NPCExecution.RunWorldNPCSweep();
	}

	public override void PostUpdateNPCs()
	{
		NPCExecution.DrainPendingDeletions();
		NPCExecution.DrainPendingExecutions();
		NPCExecution.SuppressReactivatedNPCs();
		NPCExecution.RunWorldNPCSweep();
	}

	public override void PreUpdateProjectiles()
	{
		NPCExecution.RunWorldProjectileSweep();
	}

	public override void PostUpdateProjectiles()
	{
		NPCExecution.RunWorldProjectileSweep();
	}

	public override void PostUpdateEverything()
	{
		NPCExecution.AdvanceWorldSweep();
	}

	public override void OnWorldUnload()
	{
		NPCExecution.ResetState();
	}

	public override void Unload()
	{
		NPCExecution.ResetState();
	}
}
