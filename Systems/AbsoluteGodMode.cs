using System;
using GodsHellfire_Reborn.Items;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;

namespace GodsHellfire_Reborn.Systems;

internal class AbsoluteGodPlayer : ModPlayer
{
	private bool accessoryEffectActive;
	private int lastSafeMountType = -1;
	private bool lastSafeMountFacedLeft;

	internal static void EnableForCurrentTick(Player player)
	{
		player.GetModPlayer<AbsoluteGodPlayer>().accessoryEffectActive = true;
		Restore(player);
	}

	internal static bool IsProtected(Player player)
	{
		if (player == null || Main.gameMenu)
			return false;

		// Inspect the actual functional slots as well as the normal per-tick
		// accessory flag. This closes the update-order gap where another mod can
		// try to kill the player before UpdateAccessory has run for this tick.
		if (HasAccessoryInVanillaSlot(player) || HasAccessoryInModdedSlot(player))
			return true;

		return player.GetModPlayer<AbsoluteGodPlayer>().accessoryEffectActive;
	}

	internal static void Restore(Player player)
	{
		if (!IsProtected(player))
			return;

		AbsoluteGodPlayer guard = player.GetModPlayer<AbsoluteGodPlayer>();
		bool rejectedDeath = !player.active || player.dead || player.ghost || player.statLife <= 0;

		if (!rejectedDeath)
			guard.RememberMount(player);

		// creativeGodMode is honored directly by vanilla Hurt/KillMe and is reset
		// by Player.ResetEffects, so it is intentionally restored at many stages.
		player.creativeGodMode = true;
		player.active = true;
		player.dead = false;
		player.ghost = false;
		player.respawnTimer = 0;

		player.statLifeMax2 = Math.Max(1, player.statLifeMax2);
		player.statLife = player.statLifeMax2;
		player.statMana = Math.Max(0, player.statManaMax2);
		player.breath = Math.Max(0, player.breathMax);
		player.lavaTime = Math.Max(player.lavaTime, player.lavaMax);

		player.lifeRegen = Math.Max(0, player.lifeRegen);
		player.lifeRegenCount = Math.Max(0, player.lifeRegenCount);
		player.suffocating = false;
		player.noFallDmg = true;
		player.lavaImmune = true;
		player.fireWalk = true;

		player.immune = true;
		player.immuneNoBlink = true;
		player.immuneTime = Math.Max(player.immuneTime, 2);
		player.immuneAlpha = 0;
		player.immuneAlphaDirection = 0;

		for (int i = 0; i < player.hurtCooldowns.Length; i++)
			player.hurtCooldowns[i] = Math.Max(player.hurtCooldowns[i], 2);

		// Undo the usual persistent side effects if a command or another mod
		// wrote death fields directly instead of calling Hurt/KillMe.
		player.showLastDeath = false;
		player.lostCoins = 0;
		player.lostCoinString = string.Empty;

		if (rejectedDeath && guard.lastSafeMountType >= 0 && !player.mount.Active)
			player.mount.SetMount(guard.lastSafeMountType, player, guard.lastSafeMountFacedLeft);

		guard.RememberMount(player);
		RemoveDebuffs(player);
	}

	private static bool HasAccessoryInVanillaSlot(Player player)
	{
		int end = Math.Min(10, player.armor.Length);
		for (int slot = 3; slot < end; slot++)
		{
			Item item = player.armor[slot];
			if (item != null && player.IsItemSlotUnlockedAndUsable(slot) && item.ModItem is Absolute_God)
				return true;
		}

		return false;
	}

	private static bool HasAccessoryInModdedSlot(Player player)
	{
		try
		{
			ModAccessorySlotPlayer slotPlayer = player.GetModPlayer<ModAccessorySlotPlayer>();
			AccessorySlotLoader loader = LoaderManager.Get<AccessorySlotLoader>();
			using var currentPlayer = new Main.CurrentPlayerOverride(player);

			for (int slot = 0; slot < slotPlayer.LoadedSlotCount; slot++)
			{
				if (!loader.ModdedIsSpecificItemSlotUnlockedAndUsable(slot, player, vanity: false))
					continue;

				if (loader.Get(slot, player).FunctionalItem.ModItem is Absolute_God)
					return true;
			}
		}
		catch
		{
			// A third-party accessory slot is allowed to implement arbitrary code in
			// IsEnabled. UpdateAccessory's flag remains the safe fallback if it throws.
		}

		return false;
	}

	private void RememberMount(Player player)
	{
		lastSafeMountType = player.mount.Active ? player.mount.Type : -1;
		lastSafeMountFacedLeft = player.direction < 0;
	}

	private static void RemoveDebuffs(Player player)
	{
		int immunityCount = Math.Min(player.buffImmune.Length, Main.debuff.Length);
		for (int type = 1; type < immunityCount; type++)
		{
			if (Main.debuff[type])
				player.buffImmune[type] = true;
		}

		// DelBuff compacts both buff arrays, so process them backwards.
		for (int slot = player.buffType.Length - 1; slot >= 0; slot--)
		{
			int type = player.buffType[slot];
			if (type > 0 && type < Main.debuff.Length && Main.debuff[type])
				player.DelBuff(slot);
		}
	}

	public override void ResetEffects()
	{
		accessoryEffectActive = false;
	}

	public override void PreUpdate()
	{
		Restore(Player);
	}

	public override void PreUpdateBuffs()
	{
		if (IsProtected(Player))
			RemoveDebuffs(Player);
	}

	public override void PostUpdateBuffs()
	{
		Restore(Player);
	}

	public override void UpdateEquips()
	{
		Restore(Player);
	}

	public override void PostUpdateEquips()
	{
		Restore(Player);
	}

	public override void PostUpdateMiscEffects()
	{
		Restore(Player);
	}

	public override void UpdateBadLifeRegen()
	{
		if (!IsProtected(Player))
			return;

		Player.lifeRegen = Math.Max(0, Player.lifeRegen);
		Player.lifeRegenCount = Math.Max(0, Player.lifeRegenCount);
	}

	public override void UpdateDead()
	{
		Restore(Player);
	}

	public override void UpdateAutopause()
	{
		Restore(Player);
	}

	public override void PreSavePlayer()
	{
		Restore(Player);
	}

	public override void PostUpdate()
	{
		Restore(Player);
	}

	public override bool ImmuneTo(PlayerDeathReason damageSource, int cooldownCounter, bool dodgeable)
	{
		return IsProtected(Player);
	}

	public override bool FreeDodge(Player.HurtInfo info)
	{
		return IsProtected(Player);
	}

	public override bool ConsumableDodge(Player.HurtInfo info)
	{
		return IsProtected(Player);
	}

	public override void ModifyHurt(ref Player.HurtModifiers modifiers)
	{
		if (!IsProtected(Player))
			return;

		modifiers.DisableDust();
		modifiers.DisableSound();
		modifiers.Cancel();
	}

	public override bool PreKill(double damage, int hitDirection, bool pvp, ref bool playSound, ref bool genDust, ref PlayerDeathReason damageSource)
	{
		if (!IsProtected(Player))
			return true;

		playSound = false;
		genDust = false;
		Restore(Player);
		return false;
	}
}

/// <summary>
/// Intercepts the vanilla damage/death entry points and repeatedly repairs
/// direct field writes made by commands or other mods.
/// </summary>
internal class AbsoluteGodSystem : ModSystem
{
	public override void Load()
	{
		On_Player.Hurt_PlayerDeathReason_int_int_bool_bool_bool_int_bool_float += PreventLegacyHurt;
		On_Player.Hurt_PlayerDeathReason_int_int_bool_bool_int_bool_float_float_float += PreventHurt;
		On_Player.Hurt_PlayerDeathReason_int_int_refHurtInfo_bool_bool_int_bool_float_float_float += PreventHurtWithInfo;
		On_Player.Hurt_HurtInfo_bool += PreventFinalHurt;
		On_Player.KillMe += PreventKillMe;
		On_Player.KillMeForGood += PreventKillMeForGood;
		On_Player.UpdateDead += PreventDeadUpdate;
		On_Player.DropTombstone += PreventDropTombstone;
		On_Player.DropItems += PreventDropItems;
		On_Player.DropCoins += PreventDropCoins;
	}

	public override void Unload()
	{
		On_Player.Hurt_PlayerDeathReason_int_int_bool_bool_bool_int_bool_float -= PreventLegacyHurt;
		On_Player.Hurt_PlayerDeathReason_int_int_bool_bool_int_bool_float_float_float -= PreventHurt;
		On_Player.Hurt_PlayerDeathReason_int_int_refHurtInfo_bool_bool_int_bool_float_float_float -= PreventHurtWithInfo;
		On_Player.Hurt_HurtInfo_bool -= PreventFinalHurt;
		On_Player.KillMe -= PreventKillMe;
		On_Player.KillMeForGood -= PreventKillMeForGood;
		On_Player.UpdateDead -= PreventDeadUpdate;
		On_Player.DropTombstone -= PreventDropTombstone;
		On_Player.DropItems -= PreventDropItems;
		On_Player.DropCoins -= PreventDropCoins;
	}

	private static double PreventLegacyHurt(On_Player.orig_Hurt_PlayerDeathReason_int_int_bool_bool_bool_int_bool_float orig, Player self, PlayerDeathReason damageSource, int damage, int hitDirection, bool pvp, bool quiet, bool crit, int cooldownCounter, bool dodgeable, float armorPenetration)
	{
		if (!AbsoluteGodPlayer.IsProtected(self))
			return orig(self, damageSource, damage, hitDirection, pvp, quiet, crit, cooldownCounter, dodgeable, armorPenetration);

		AbsoluteGodPlayer.Restore(self);
		return 0d;
	}

	private static double PreventHurt(On_Player.orig_Hurt_PlayerDeathReason_int_int_bool_bool_int_bool_float_float_float orig, Player self, PlayerDeathReason damageSource, int damage, int hitDirection, bool pvp, bool quiet, int cooldownCounter, bool dodgeable, float armorPenetration, float scalingArmorPenetration, float knockback)
	{
		if (!AbsoluteGodPlayer.IsProtected(self))
			return orig(self, damageSource, damage, hitDirection, pvp, quiet, cooldownCounter, dodgeable, armorPenetration, scalingArmorPenetration, knockback);

		AbsoluteGodPlayer.Restore(self);
		return 0d;
	}

	private static double PreventHurtWithInfo(On_Player.orig_Hurt_PlayerDeathReason_int_int_refHurtInfo_bool_bool_int_bool_float_float_float orig, Player self, PlayerDeathReason damageSource, int damage, int hitDirection, out Player.HurtInfo info, bool pvp, bool quiet, int cooldownCounter, bool dodgeable, float armorPenetration, float scalingArmorPenetration, float knockback)
	{
		if (!AbsoluteGodPlayer.IsProtected(self))
			return orig(self, damageSource, damage, hitDirection, out info, pvp, quiet, cooldownCounter, dodgeable, armorPenetration, scalingArmorPenetration, knockback);

		info = new Player.HurtInfo
		{
			DamageSource = damageSource,
			PvP = pvp,
			CooldownCounter = cooldownCounter,
			Dodgeable = dodgeable,
			HitDirection = hitDirection,
			Cancelled = true,
			DustDisabled = true,
			SoundDisabled = true
		};
		AbsoluteGodPlayer.Restore(self);
		return 0d;
	}

	private static void PreventFinalHurt(On_Player.orig_Hurt_HurtInfo_bool orig, Player self, Player.HurtInfo info, bool quiet)
	{
		if (AbsoluteGodPlayer.IsProtected(self))
		{
			AbsoluteGodPlayer.Restore(self);
			return;
		}

		orig(self, info, quiet);
	}

	private static void PreventKillMe(On_Player.orig_KillMe orig, Player self, PlayerDeathReason damageSource, double damage, int hitDirection, bool pvp)
	{
		if (AbsoluteGodPlayer.IsProtected(self))
		{
			AbsoluteGodPlayer.Restore(self);
			return;
		}

		orig(self, damageSource, damage, hitDirection, pvp);
	}

	private static void PreventKillMeForGood(On_Player.orig_KillMeForGood orig, Player self)
	{
		if (AbsoluteGodPlayer.IsProtected(self))
		{
			AbsoluteGodPlayer.Restore(self);
			return;
		}

		orig(self);
	}

	private static void PreventDeadUpdate(On_Player.orig_UpdateDead orig, Player self)
	{
		if (AbsoluteGodPlayer.IsProtected(self))
		{
			AbsoluteGodPlayer.Restore(self);
			return;
		}

		orig(self);
	}

	private static void PreventDropTombstone(On_Player.orig_DropTombstone orig, Player self, long coinsOwned, NetworkText deathText, int hitDirection)
	{
		if (!AbsoluteGodPlayer.IsProtected(self))
			orig(self, coinsOwned, deathText, hitDirection);
	}

	private static void PreventDropItems(On_Player.orig_DropItems orig, Player self)
	{
		if (!AbsoluteGodPlayer.IsProtected(self))
			orig(self);
	}

	private static long PreventDropCoins(On_Player.orig_DropCoins orig, Player self)
	{
		return AbsoluteGodPlayer.IsProtected(self) ? 0L : orig(self);
	}

	public override void PreUpdateEntities()
	{
		RestoreProtectedPlayers();
	}

	public override void PreUpdatePlayers()
	{
		RestoreProtectedPlayers();
	}

	public override void PostUpdatePlayers()
	{
		RestoreProtectedPlayers();
	}

	public override void PostUpdateEverything()
	{
		RestoreProtectedPlayers();
	}

	public override void PreSaveAndQuit()
	{
		RestoreProtectedPlayers();
	}

	internal static void RestoreProtectedPlayers()
	{
		if (Main.gameMenu)
			return;

		// This mod targets single player. Avoid walking all 255 player slots at
		// each IL/system boundary while retaining the multiplayer-safe fallback.
		if (Main.netMode == NetmodeID.SinglePlayer)
		{
			if (Main.myPlayer >= 0 && Main.myPlayer < Main.maxPlayers)
				AbsoluteGodPlayer.Restore(Main.player[Main.myPlayer]);
			return;
		}

		for (int i = 0; i < Main.maxPlayers; i++)
		{
			Player player = Main.player[i];
			if (player == null || (i != Main.myPlayer && !player.active && !player.dead))
				continue;

			// Restore performs the protection check itself. Calling it directly
			// avoids scanning vanilla/modded accessory slots twice for protected
			// players at every outer boundary.
			AbsoluteGodPlayer.Restore(player);
		}
	}
}
