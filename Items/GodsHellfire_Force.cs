using GodsHellfire_Reborn.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Items;

public class GodsHellfire_Force : GodsHellfire
{
	public override void SetDefaults()
	{
		base.SetDefaults();
		Item.damage = 666;
		Item.shoot = ProjectileID.DD2SquireSonicBoom;
	}

	public override void OnHitNPC(Player player, NPC target, NPC.HitInfo hit, int damageDone)
	{
		NPCExecution.Execute(target);
	}

	protected override void PerformWorldClear()
	{
		NPCExecution.BeginDeletionSweep();
	}

	public override void UseItemHitbox(Player player, ref Rectangle hitbox, ref bool noHitbox)
	{
		if (noHitbox || player.altFunctionUse == 2)
			return;

		// UseItemHitbox is reached even when vanilla rejects an NPC as a damage
		// target. The intersection therefore supplies Force's "forced hit" path.
		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (NPCExecution.CanExecute(npc) && hitbox.Intersects(npc.Hitbox))
				NPCExecution.Execute(npc);
		}
	}
}
