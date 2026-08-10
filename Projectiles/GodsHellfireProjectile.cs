using System.IO;
using GodsHellfire_Reborn.Items;
using GodsHellfire_Reborn.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace GodsHellfire_Reborn.Projectiles;

/// <summary>
/// Adds God's Hellfire behavior to individual vanilla sonic-boom projectiles.
///
/// The weapon deliberately uses ProjectileID.DD2SquireSonicBoom (684) instead
/// of registering a ModProjectile. Mod projectile numeric IDs are assigned at
/// load time, so values such as 1064/1065 can refer to completely different
/// projectiles after another mod is enabled.
/// </summary>
public class HellfireProjectileBehavior : GlobalProjectile
{
	private enum HellfireMode : byte
	{
		None,
		Normal,
		Force
	}

	private HellfireMode mode;

	public override bool InstancePerEntity => true;

	public override bool AppliesToEntity(Projectile entity, bool lateInstantiation)
	{
		return entity.type == ProjectileID.DD2SquireSonicBoom;
	}

	public override void OnSpawn(Projectile projectile, IEntitySource source)
	{
		if (source is not IEntitySource_WithStatsFromItem itemSource)
			return;

		if (itemSource.Item.ModItem is GodsHellfire_Force)
			mode = HellfireMode.Force;
		else if (itemSource.Item.ModItem is Items.GodsHellfire)
			mode = HellfireMode.Normal;
	}

	public override void AI(Projectile projectile)
	{
		if (mode != HellfireMode.Force)
			return;

		Lighting.AddLight(projectile.Center, 0f, 0.8f, 0.8f);

		// Vanilla projectile damage skips dontTakeDamage/immortal and several
		// other non-hittable states before OnHitNPC. Force scans the exact same
		// collision shape itself so those targets still count as struck.
		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (NPCExecution.CanExecute(npc) && Collides(projectile, npc.Hitbox))
				NPCExecution.Execute(npc, forceParent: true);
		}
	}

	public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
	{
		if (mode != HellfireMode.None)
			NPCExecution.Execute(target, forceParent: mode == HellfireMode.Force);
	}

	public override Color? GetAlpha(Projectile projectile, Color lightColor)
	{
		if (mode != HellfireMode.Force)
			return null;

		Color cyan = Color.Cyan;
		cyan.A = (byte)(255f * projectile.Opacity);
		return cyan;
	}

	public override void SendExtraAI(Projectile projectile, BitWriter bitWriter, BinaryWriter binaryWriter)
	{
		binaryWriter.Write((byte)mode);
	}

	public override void ReceiveExtraAI(Projectile projectile, BitReader bitReader, BinaryReader binaryReader)
	{
		mode = (HellfireMode)binaryReader.ReadByte();
	}

	private static bool Collides(Projectile projectile, Rectangle targetHitbox)
	{
		// Exact collision shape used by vanilla DD2SquireSonicBoom.
		Vector2 perpendicular = projectile.velocity
			.SafeNormalize(Vector2.UnitY)
			.RotatedBy(-MathHelper.PiOver2) * projectile.scale;
		float collisionPoint = 0f;

		return Collision.CheckAABBvLineCollision(
			targetHitbox.TopLeft(),
			targetHitbox.Size(),
			projectile.Center - perpendicular * 40f,
			projectile.Center + perpendicular * 40f,
			16f * projectile.scale,
			ref collisionPoint);
	}
}
