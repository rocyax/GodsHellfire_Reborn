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
	// Some NPCs deliberately draw shields, forcefields, or oversized bodies a
	// little beyond their logical hitbox. Force is an administrator weapon, so
	// tolerate a modest visual/logical mismatch without turning contact into a
	// proximity-wide execution. Normal Hellfire projectiles remain unchanged.
	private const int ForceContactPadding = 32;

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
		// other non-hittable states before OnHitNPC. Force therefore performs its
		// own contact scan. GlobalProjectile.AI runs before Projectile updates its
		// position, so test both the current and imminent visual positions.
		for (int i = 0; i < Main.maxNPCs; i++)
		{
			NPC npc = Main.npc[i];
			if (NPCExecution.CanExecute(npc) && ForceCollides(projectile, npc.Hitbox))
				NPCExecution.Execute(npc);
		}
	}

	public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
	{
		if (mode != HellfireMode.None)
			NPCExecution.Execute(target);
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

	private static bool ForceCollides(Projectile projectile, Rectangle targetHitbox)
	{
		// The padding covers common visual/logical hitbox discrepancies while
		// retaining an actual overlap requirement. It is intentionally applied
		// only to Force's manual path, never to Terraria's normal damage system.
		Rectangle contactHitbox = targetHitbox;
		contactHitbox.Inflate(ForceContactPadding, ForceContactPadding);

		Vector2 currentCenter = projectile.Center;
		if (CollidesAt(projectile, contactHitbox, currentCenter))
			return true;

		Vector2 imminentCenter = currentCenter + projectile.velocity;
		return imminentCenter != currentCenter &&
			CollidesAt(projectile, contactHitbox, imminentCenter);
	}

	private static bool CollidesAt(Projectile projectile, Rectangle targetHitbox, Vector2 projectileCenter)
	{
		// Exact collision shape used by vanilla DD2SquireSonicBoom.
		Vector2 perpendicular = projectile.velocity
			.SafeNormalize(Vector2.UnitY)
			.RotatedBy(-MathHelper.PiOver2) * projectile.scale;
		float collisionPoint = 0f;

		return Collision.CheckAABBvLineCollision(
			targetHitbox.TopLeft(),
			targetHitbox.Size(),
			projectileCenter - perpendicular * 40f,
			projectileCenter + perpendicular * 40f,
			16f * projectile.scale,
			ref collisionPoint);
	}
}
