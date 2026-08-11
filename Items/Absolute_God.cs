using GodsHellfire_Reborn.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GodsHellfire_Reborn.Items;

public class Absolute_God : ModItem
{
	public override string Texture => "GodsHellfire_Reborn/Items/Absolute_God_Accessory";

	public override void SetDefaults()
	{
		Item.width = 64;
		Item.height = 64;
		Item.accessory = true;
		Item.value = 0;
		Item.rare = ItemRarityID.Expert;
	}

	public override void UpdateAccessory(Player player, bool hideVisual)
	{
		AbsoluteGodPlayer.EnableForCurrentTick(player);
	}
}
