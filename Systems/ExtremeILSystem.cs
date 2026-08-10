using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Systems;

/// <summary>
/// Narrow IL return barriers which close gaps left by ordinary ModLoader hook
/// ordering. Every edit is conditional at runtime, so unrelated combat and
/// unprotected players retain the original return values and behavior.
/// </summary>
internal class ExtremeILSystem : ModSystem
{
	public override void Load()
	{
		TryInstall(
			typeof(NPCLoader),
			nameof(NPCLoader.CheckDead),
			BindingFlags.Public | BindingFlags.Static,
			new[] { typeof(NPC) },
			PatchNPCDeathGateReturns,
			"NPCLoader.CheckDead execution barrier");

		TryInstall(
			typeof(NPCLoader),
			nameof(NPCLoader.PreKill),
			BindingFlags.Public | BindingFlags.Static,
			new[] { typeof(NPC) },
			PatchNPCDeathGateReturns,
			"NPCLoader.PreKill execution barrier");

		// PlayerLoader.PostUpdate closes the load-order gap between ModPlayer
		// callbacks. Player.Update is the wider per-player boundary and catches
		// direct field writes injected outside the normal ModPlayer hooks.
		TryInstall(
			typeof(PlayerLoader),
			nameof(PlayerLoader.PostUpdate),
			BindingFlags.Public | BindingFlags.Static,
			new[] { typeof(Player) },
			PatchStaticPlayerBoundaryReturns,
			"PlayerLoader.PostUpdate Absolute_God barrier");

		TryInstall(
			typeof(Player),
			nameof(Player.Update),
			BindingFlags.Public | BindingFlags.Instance,
			new[] { typeof(int) },
			PatchInstancePlayerBoundaryReturns,
			"Player.Update Absolute_God barrier");

		// These close the gaps on both sides of the vanilla player loop. In
		// particular, the pre-update barrier repairs active=false before Main can
		// skip Player.Update entirely.
		TryInstall(
			typeof(SystemLoader),
			nameof(SystemLoader.PreUpdatePlayers),
			BindingFlags.Public | BindingFlags.Static,
			Type.EmptyTypes,
			PatchSystemBoundaryReturns,
			"SystemLoader.PreUpdatePlayers Absolute_God barrier");

		TryInstall(
			typeof(SystemLoader),
			nameof(SystemLoader.PostUpdatePlayers),
			BindingFlags.Public | BindingFlags.Static,
			Type.EmptyTypes,
			PatchSystemBoundaryReturns,
			"SystemLoader.PostUpdatePlayers Absolute_God barrier");

		// This runs after every ModSystem.PostUpdateEverything callback, not just
		// at this mod's position in the ModSystem hook list.
		TryInstall(
			typeof(SystemLoader),
			nameof(SystemLoader.PostUpdateEverything),
			BindingFlags.Public | BindingFlags.Static,
			Type.EmptyTypes,
			PatchSystemBoundaryReturns,
			"SystemLoader.PostUpdateEverything Absolute_God barrier");
	}

	private void TryInstall(
		Type declaringType,
		string methodName,
		BindingFlags bindingFlags,
		Type[] parameterTypes,
		ILContext.Manipulator manipulator,
		string description)
	{
		MethodInfo method = declaringType.GetMethod(methodName, bindingFlags, null, parameterTypes, null);
		if (method == null)
		{
			Mod.Logger.Warn($"Could not find {description}; the existing non-IL fallback remains active.");
			return;
		}

		try
		{
			MonoModHooks.Modify(method, manipulator);
			Mod.Logger.Info($"Installed {description}.");
		}
		catch (Exception exception)
		{
			// A tModLoader update or another IL edit must not make the whole mod
			// unloadable. Existing execution/deactivation and On_ detours remain as
			// the compatibility fallback if an optional outer barrier cannot apply.
			Mod.Logger.Warn($"Could not install {description}; the existing non-IL fallback remains active.\n{exception}");
		}
	}

	private static void PatchNPCDeathGateReturns(ILContext il)
	{
		PatchEveryReturn(
			il,
			cursor =>
			{
				// The original bool is already on the evaluation stack at ret.
				cursor.Emit(OpCodes.Ldarg_0);
				cursor.EmitDelegate<Func<bool, NPC, bool>>(NPCExecution.OverrideDeathGate);
			},
			"NPC death gate");
	}

	private static void PatchStaticPlayerBoundaryReturns(ILContext il)
	{
		PatchEveryReturn(
			il,
			cursor =>
			{
				cursor.Emit(OpCodes.Ldarg_0);
				cursor.EmitDelegate<Action<Player>>(AbsoluteGodPlayer.Restore);
			},
			"static player update boundary");
	}

	private static void PatchInstancePlayerBoundaryReturns(ILContext il)
	{
		PatchEveryReturn(
			il,
			cursor =>
			{
				cursor.Emit(OpCodes.Ldarg_0);
				cursor.EmitDelegate<Action<Player>>(AbsoluteGodPlayer.Restore);
			},
			"instance player update boundary");
	}

	private static void PatchSystemBoundaryReturns(ILContext il)
	{
		PatchEveryReturn(
			il,
			cursor => cursor.EmitDelegate<Action>(AbsoluteGodSystem.RestoreProtectedPlayers),
			"system update boundary");
	}

	private static void PatchEveryReturn(ILContext il, Action<ILCursor> emitBarrier, string description)
	{
		var cursor = new ILCursor(il);
		int patchedReturns = 0;

		// MoveType.AfterLabel makes ILCursor retarget ILLabel objects and the
		// TryEnd/HandlerEnd boundaries of any exception handler that ends at this
		// ret, so the barrier is never emitted inside a handler region.
		while (cursor.TryGotoNext(MoveType.AfterLabel, instruction => instruction.OpCode == OpCodes.Ret))
		{
			Instruction originalReturn = cursor.Next;
			int barrierStartIndex = cursor.Index;

			emitBarrier(cursor);

			// ILCursor only retargets ILLabel operands. A method body imported from
			// the assembly still branches with raw Cecil Instruction operands, so a
			// branch/leave aimed straight at this ret would jump over the barrier.
			// Terraria.Player.Update is exactly that shape: one ret, reached by four
			// leave instructions. Redirect them onto the first emitted instruction.
			if (cursor.Index > barrierStartIndex)
				RedirectRawBranches(il, originalReturn, il.Instrs[barrierStartIndex]);

			patchedReturns++;

			// The emitted instructions are immediately before the original ret.
			// Advance over that ret so the next search cannot match it again.
			cursor.Index++;
		}

		if (patchedReturns == 0)
		{
			var cause = new InvalidOperationException($"No return instruction was found for the {description}.");
			throw new ILPatchFailureException(
				ModContent.GetInstance<global::GodsHellfire_Reborn.GodsHellfire_Reborn>(),
				il,
				cause);
		}
	}

	/// <summary>
	/// Repoints raw Cecil branch operands from <paramref name="originalReturn"/> to
	/// <paramref name="barrierStart"/>. Only reachability changes: a branch which
	/// previously landed on the ret now runs the barrier and falls through into the
	/// same ret. A leave leaving a protected region still executes its finally
	/// blocks first, because the barrier sits outside those regions.
	/// </summary>
	private static void RedirectRawBranches(ILContext il, Instruction originalReturn, Instruction barrierStart)
	{
		foreach (Instruction instruction in il.Instrs)
		{
			if (ReferenceEquals(instruction, barrierStart))
				continue;

			if (ReferenceEquals(instruction.Operand, originalReturn))
			{
				instruction.Operand = barrierStart;
				continue;
			}

			// A switch stores an operand array; rewrite only the matching entries.
			if (instruction.Operand is Instruction[] targets)
			{
				for (int i = 0; i < targets.Length; i++)
				{
					if (ReferenceEquals(targets[i], originalReturn))
						targets[i] = barrierStart;
				}
			}
		}
	}
}
