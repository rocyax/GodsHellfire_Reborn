using GodsHellfire_Reborn.Systems;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Items;

public class GodsHellfire : ModItem
{
	private static readonly SoundStyle WorldClearSound = new("GodsHellfire_Reborn/assets/Dice_Roll_Game");
	private bool worldClearUse;

	public override void SetDefaults()
	{
		Item.damage = 333;
		Item.crit = 100;
		Item.DamageType = DamageClass.Melee;
		Item.width = 80;
		Item.height = 80;
		Item.useTime = 20;
		Item.useAnimation = 20;
		Item.useStyle = ItemUseStyleID.Swing;
		Item.knockBack = 6f;
		Item.value = 99000000;
		Item.rare = ItemRarityID.Master;
		Item.UseSound = SoundID.DD2_BetsyFlameBreath;
		Item.autoReuse = true;
		worldClearUse = false;
		Item.useTurn = true;
		Item.noMelee = false;
		// Use the fixed vanilla projectile type. Mod projectile IDs are allocated
		// dynamically and change whenever the enabled mod set/load order changes.
		// HellfireProjectileBehavior tags only this weapon's spawned instances.
		Item.shoot = ProjectileID.DD2SquireSonicBoom;
		Item.shootSpeed = 35f;
	}

	public override bool AltFunctionUse(Player player)
	{
		return true;
	}

	public override bool CanUseItem(Player player)
	{
		bool clearingWorld = player.altFunctionUse == 2;
		worldClearUse = clearingWorld;

		// Right click still displays the swing, but it cannot deal a normal
		// melee hit or enter the projectile-spawning path.
		Item.noMelee = clearingWorld;
		Item.shoot = clearingWorld
			? ProjectileID.None
			: ProjectileID.DD2SquireSonicBoom;
		Item.UseSound = clearingWorld
			? null
			: SoundID.DD2_BetsyFlameBreath;
		Item.autoReuse = !clearingWorld;

		return base.CanUseItem(player);
	}

	public override bool CanShoot(Player player)
	{
		return player.altFunctionUse != 2;
	}

	public override bool? CanAutoReuseItem(Player player)
	{
		// A false hook result overrides Item.autoReuse and accessories which
		// grant autoswing. Left click retains the weapon's normal autoReuse.
		return worldClearUse ? false : null;
	}

	public override void UseAnimation(Player player)
	{
		if (player.altFunctionUse != 2)
			return;

		SoundEngine.PlaySound(WorldClearSound, player.Center);
		PerformWorldClear();
	}

	protected virtual void PerformWorldClear()
	{
		NPCExecution.BeginExecutionSweep();
	}

	public override void OnHitNPC(Player player, NPC target, NPC.HitInfo hit, int damageDone)
	{
		NPCExecution.Execute(target, forceParent: false);
	}
}
