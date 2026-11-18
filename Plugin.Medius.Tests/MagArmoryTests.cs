using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Extensions.Zipper;

namespace Plugin.Medius.Tests;

/// <summary>The Supply Depot: CP, purchases and refunds, as 2.12's lobby emulator does them.</summary>
public sealed class MagArmoryTests
{
    private static readonly MagArmory Armory = MagArmory.Embedded(MagClientVersion.Patch212)!;

    private static MagArmoryItem Item(int faction, string name) =>
        Armory.Factions.Single(f => f.Faction == faction).Items.Single(i => i.Name == name);

    [Fact]
    public void Only_builds_with_a_Supply_Depot_have_an_armory_and_its_messages()
    {
        Assert.Null(MagArmory.Embedded(MagClientVersion.Retail));
        Assert.Null(MagClientVersion.Retail.Messages.EconomyPurchaseRequest);
        MagMessageIds ids = MagClientVersion.Patch212.Messages;
        Assert.Equal((ushort)0x275, ids.EconomyPurchaseRequest);
        Assert.Equal((ushort)0x276, ids.EconomyPurchaseResponse);
        Assert.Equal((ushort)0x277, ids.EconomyRefundRequest);
        Assert.Equal((ushort)0x278, ids.EconomyRefundResponse);
        Assert.Equal((ushort)0x279, ids.RetrieveWalletRequest);
        Assert.Equal((ushort)0x27a, ids.RetrieveWalletResponse);
        Assert.Equal(0.5f, Armory.RefundFactor);
        Assert.Equal([0, 1, 2], Armory.Factions.Select(f => f.Faction).Order());
    }

    [Fact]
    public void A_skill_purchase_is_a_u8_in_1_00_and_a_u16_in_2_12()
    {
        Assert.Equal(19, MapsReaders.PurchaseSkillRequest(new ZipperBitReader([0x13]), MagClientVersion.Retail));
        Assert.Equal(19, MapsReaders.PurchaseSkillRequest(new ZipperBitReader([0x00, 0x13]), MagClientVersion.Patch212));   // live: 02680013
        Assert.Equal(150, MapsReaders.PurchaseSkillRequest(new ZipperBitReader([0x00, 0x96]), MagClientVersion.Patch212));
    }

    [Fact]
    public void The_cash_fields_are_the_record_tail_words()
    {
        MagCharacter soldier = new() { PurchasedCash = 300, AwardedCash = 1200, DeferredCash = 7, DeferredExperience = 9 };
        Assert.Equal([0u, 300u, 1200u, 7u, 9u], soldier.Unknown238);
        Assert.Equal(1500u, soldier.Cash);
    }

    [Fact]
    public void Spending_takes_earned_CP_first_and_refuses_what_both_kinds_cannot_cover()
    {
        MagCharacter soldier = new() { PurchasedCash = 500, AwardedCash = 300 };
        Assert.False(soldier.SpendCash(801));
        Assert.Equal(800u, soldier.Cash);
        Assert.True(soldier.SpendCash(400));
        Assert.Equal(0u, soldier.AwardedCash);
        Assert.Equal(400u, soldier.PurchasedCash);
    }

    [Fact]
    public void A_purchase_sets_the_item_and_spends_its_price_once()
    {
        MagArmoryItem kriss = Item(2, "kriss");                      // SVER, 2000 CP
        MagCharacter soldier = new() { Faction = 2, AwardedCash = 2500 };
        Assert.True(soldier.TryPurchaseItem((ushort)kriss.Bit, Armory, out _));
        Assert.True(soldier.HasItem(kriss.Bit));
        Assert.Equal(500u, soldier.Cash);
        Assert.False(soldier.TryPurchaseItem((ushort)kriss.Bit, Armory, out string again));
        Assert.Contains("already owned", again);
        Assert.Equal(500u, soldier.Cash);
    }

    [Fact]
    public void A_purchase_is_refused_when_short_free_or_from_another_PMC()
    {
        MagCharacter poor = new() { Faction = 2, AwardedCash = 1999 };
        Assert.False(poor.TryPurchaseItem((ushort)Item(2, "kriss").Bit, Armory, out _));
        Assert.Equal(1999u, poor.Cash);
        Assert.False(poor.TryPurchaseItem((ushort)Item(2, "ak_74u").Bit, Armory, out string free));   // price 0
        Assert.Contains("isn't for sale", free);
        Assert.False(poor.TryPurchaseItem(0x3ff, Armory, out string unknown));
        Assert.Contains("armory", unknown);
    }

    [Fact]
    public void Buying_a_group_parent_gives_its_variants_and_a_refund_takes_them_back_at_half_price()
    {
        MagArmoryItem parent = Item(2, "scope_reddot_red");
        int[] variants = [.. Armory.GroupOf(2, parent).Select(i => i.Bit)];
        Assert.Equal(3, variants.Length);                           // _shotgun, _assault, _mg
        MagCharacter soldier = new() { Faction = 2, AwardedCash = parent.Price };
        soldier.Loadouts[0].Items[3] = (ushort)variants[0];
        Assert.True(soldier.TryPurchaseItem((ushort)parent.Bit, Armory, out _));
        Assert.All(variants, bit => Assert.True(soldier.HasItem(bit)));
        Assert.Equal(0u, soldier.Cash);

        Assert.True(soldier.TryRefundItem((ushort)parent.Bit, Armory, out _));
        Assert.False(soldier.HasItem(parent.Bit));
        Assert.All(variants, bit => Assert.False(soldier.HasItem(bit)));
        Assert.Equal(MagLoadout.Empty, soldier.Loadouts[0].Items[3]);
        Assert.Equal(parent.Price / 2, soldier.AwardedCash);
    }

    [Fact]
    public void The_purchase_response_carries_the_record_only_on_success_and_the_wallet_is_empty()
    {
        MagClientVersion version = MagClientVersion.Patch212;
        byte[] refused = MapsWriters.EconomyResponse(version, version.Messages.EconomyPurchaseResponse!.Value, false, new MagCharacter());
        Assert.Equal(3, refused.Length);                              // type, then the success bit
        byte[] wallet = MapsWriters.RetrieveWalletResponse(version.Messages.RetrieveWalletResponse!.Value);
        Assert.Equal(2 + (1 + 32 * MapsWriters.WalletWords + 7) / 8, wallet.Length);
        ZipperBitReader reader = new(wallet);
        Assert.Equal((ushort)0x27a, reader.ReadU16());
        Assert.True(reader.ReadBool());
    }
}
