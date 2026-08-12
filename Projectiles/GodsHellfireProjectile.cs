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
	// DD2SquireSonicBoom has a 16x16 entity hitbox, but vanilla damage uses an
	// 80x16 line perpendicular to its velocity instead.
	private const float SonicBoomHalfLength = 40f;
	private const float SonicBoomHalfWidth = 8f;

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

	public override bool PreAI(Projectile projectile)
	{
		if (mode == HellfireMode.Force)
		{
			Lighting.AddLight(projectile.Center, 0f, 0.8f, 0.8f);
			EnforceForceContact(projectile);
		}

		// GlobalProjectile.PreAI hooks are all evaluated by tModLoader, even when
		// another mod returns false and suppresses vanilla/GlobalProjectile.AI.
		// Never veto the projectile's own AI from this behavior.
		return true;
	}

	public override void PostAI(Projectile projectile)
	{
		if (mode == HellfireMode.Force)
			EnforceForceContact(projectile);
	}

	private static void EnforceForceContact(Projectile projectile)
	{
		if (!projectile.active)
			return;

		// Vanilla projectile damage skips dontTakeDamage/immortal and several
		// other non-hittable states before OnHitNPC. Force therefore performs its
		// own contact scan. PreAI catches contact even when another mod suppresses
		// the normal AI path; PostAI catches a position changed later in that path.
		// The imminent position also covers movement performed after ProjectileAI.
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
		// Terraria's thick-line helper misses the containment case where the
		// complete 80x16 sonic-boom line lies inside a large target AABB. That is
		// especially visible against Supreme Calamitas' enlarged forcefield
		// hitbox: crossing an edge succeeds while passing through its center can
		// fail. Test the exact swept rectangle with SAT instead. Sweeping from the
		// current to imminent center also prevents high-speed tunnelling.
		Vector2 forward = projectile.velocity.SafeNormalize(Vector2.UnitY);
		Vector2 perpendicular = forward.RotatedBy(-MathHelper.PiOver2);
		float scale = System.MathF.Abs(projectile.scale);
		float travel = projectile.velocity.Length();
		Vector2 sweepCenter = projectile.Center + projectile.velocity * 0.5f;
		float halfForwardExtent = travel * 0.5f + SonicBoomHalfWidth * scale;
		float halfPerpendicularExtent = SonicBoomHalfLength * scale;

		return OrientedRectangleIntersectsAabb(
			targetHitbox,
			sweepCenter,
			forward,
			perpendicular,
			halfForwardExtent,
			halfPerpendicularExtent);
	}

	private static bool OrientedRectangleIntersectsAabb(
		Rectangle targetHitbox,
		Vector2 rectangleCenter,
		Vector2 forward,
		Vector2 perpendicular,
		float halfForwardExtent,
		float halfPerpendicularExtent)
	{
		Vector2 targetCenter = new(
			targetHitbox.Left + targetHitbox.Width * 0.5f,
			targetHitbox.Top + targetHitbox.Height * 0.5f);
		Vector2 targetHalfExtents = new(
			targetHitbox.Width * 0.5f,
			targetHitbox.Height * 0.5f);
		Vector2 centerDelta = targetCenter - rectangleCenter;

		// SAT for an oriented projectile rectangle against a world-axis-aligned
		// NPC rectangle. All four distinct edge normals must overlap. Unlike the
		// vanilla thick-line routine, this also accepts either rectangle fully
		// containing the other.
		return OverlapsOnAxis(
			centerDelta,
			targetHalfExtents,
			forward,
			perpendicular,
			halfForwardExtent,
			halfPerpendicularExtent,
			Vector2.UnitX) &&
			OverlapsOnAxis(
				centerDelta,
				targetHalfExtents,
				forward,
				perpendicular,
				halfForwardExtent,
				halfPerpendicularExtent,
				Vector2.UnitY) &&
			OverlapsOnAxis(
				centerDelta,
				targetHalfExtents,
				forward,
				perpendicular,
				halfForwardExtent,
				halfPerpendicularExtent,
				forward) &&
			OverlapsOnAxis(
				centerDelta,
				targetHalfExtents,
				forward,
				perpendicular,
				halfForwardExtent,
				halfPerpendicularExtent,
				perpendicular);
	}

	private static bool OverlapsOnAxis(
		Vector2 centerDelta,
		Vector2 targetHalfExtents,
		Vector2 forward,
		Vector2 perpendicular,
		float halfForwardExtent,
		float halfPerpendicularExtent,
		Vector2 axis)
	{
		float targetRadius =
			targetHalfExtents.X * System.MathF.Abs(axis.X) +
			targetHalfExtents.Y * System.MathF.Abs(axis.Y);
		float projectileRadius =
			halfForwardExtent * System.MathF.Abs(Vector2.Dot(forward, axis)) +
			halfPerpendicularExtent * System.MathF.Abs(Vector2.Dot(perpendicular, axis));
		float centerDistance = System.MathF.Abs(Vector2.Dot(centerDelta, axis));

		return centerDistance <= targetRadius + projectileRadius;
	}
}
