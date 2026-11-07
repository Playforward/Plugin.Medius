using Plugin.Medius.Extensions.Zipper;
using System.Text;
using System.Text.Json.Serialization;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// A MAG soldier as the server keeps it. The fields map onto what the
/// client sends in MagNetMessageCreateCharacterRequest (demarshal mage_g
/// 0x8c39dc) and reads back in MagNetCharacterRecord (0x8bda40). Field
/// names are the client's own, from the record's Dump (0x8be900..) [C].
/// </summary>
public sealed class MagCharacter
{
    public string Name { get; set; } = "";
    /// <summary>
    /// Signed byte the client reads with 0x17de30; the PMC: Valor, Raven, SVER
    /// = 0, 1, 2 (kCharacterFactionGreen/Black/Red, whose Lua values are set at 0xb2ff4) [C].
    /// </summary>
    public sbyte Faction { get; set; }
    /// <summary>m_headID: the create request's first byte, stored by 0x8a4e14 at zServer record +0x30 [C].</summary>
    [JsonPropertyName("Choice1")]
    public byte HeadId { get; set; }
    /// <summary>m_voiceID: the create request's second byte (zServer record +0x34) [C].</summary>
    [JsonPropertyName("Choice2")]
    public byte VoiceId { get; set; }
    /// <summary>m_universeID: the create request's u32 (zServer record +0x1c4) [C].</summary>
    [JsonPropertyName("Choice3")]
    public uint UniverseId { get; set; }
    /// <summary>The player's SceNpId (36 bytes), as the client sent it.</summary>
    public byte[] NpId { get; set; } = new byte[36];
    public uint Id { get; set; } = 1;

    public byte Level { get; set; } = 1;
    public uint Experience { get; set; }
    /// <summary>m_skillPoints, a u8 on the wire; the client's adders clamp it to 0..255 (0x8a82e8).</summary>
    public byte SkillPoints { get; set; }
    /// <summary>
    /// m_skills: the purchased skills, a 128-bit set (bit <c>id &amp; 31</c> of
    /// word <c>id &gt;&gt; 5</c>, as PurchaseSkill sets it at 0x8a3244) [C].
    /// </summary>
    public uint[] Skills { get; set; } = new uint[SkillWords];
    /// <summary>
    /// m_availableItems: 768 bits of unlocked gear. Buying a skill sets the
    /// bits of the items it unlocks (0x8a32a8) and refunding it clears them
    /// (0x8a3170); without a skill table only a respec touches these (clearing all).
    /// </summary>
    public uint[] AvailableItems { get; set; } = new uint[ItemWords];
    /// <summary>
    /// m_skillSpecialty, which the server derives from the skills (0x8a2d44)
    /// and keeps here; 4 (Commando) until the table makes it something else.
    /// </summary>
    public byte SkillSpecialty { get; set; } = MagSkillTable.DefaultSpecialty;
    public byte RespecCount { get; set; }
    public uint RespecPoints { get; set; }
    /// <summary>m_factionHistory: one bit per PMC played (1, 2, 4), set by SetFaction (0x8a826c) [C].</summary>
    public byte FactionHistory { get; set; }
    /// <summary>
    /// m_factionReSpecAvailable. The emulator never sets it and allows a
    /// faction respec regardless ("cannot change factions at this time, but
    /// we'll allow it"); on here so the front end offers the option.
    /// </summary>
    public bool FactionReSpecAvailable { get; set; } = true;
    /// <summary>m_lastUsedLoadout: set by SelectLoadout (0x252); the character select shows this loadout.</summary>
    public byte LastUsedLoadout { get; set; }
    public byte VeteranLevel { get; set; }
    /// <summary>m_flags; a faction or veteran respec that changes PMC sets <see cref="FlagFactionChanged"/> (0x8a839c).</summary>
    public byte Flags { get; set; }

    /// <summary>The five loadouts; edited through LoadoutUpdateRequest.</summary>
    public MagLoadout[] Loadouts { get; set; } = [new(), new(), new(), new(), new()];

    /// <summary>
    /// 2.12's record tail (reader 0x70e914, after m_flags at +0x228): six u16
    /// at +0x22a..+0x234. 2.x strips the record's Dump strings, so their names
    /// are unknown; the Supply Depot (economy) fields are the likely
    /// candidates [guess]. Zero until decoded.
    /// </summary>
    public ushort[] Unknown22A { get; set; } = new ushort[6];

    /// <summary>2.12's record tail: five u32 at +0x238..+0x248 (see <see cref="Unknown22A"/>).</summary>
    public uint[] Unknown238 { get; set; } = new uint[5];

    /// <summary>
    /// The bool 2.12's CreateCharacterRequest adds after the NpId (marshal
    /// 0x7101e4, +0xb0, one bit via 0xb89924); meaning unknown, kept for the log.
    /// </summary>
    [JsonIgnore]
    public bool CreateRequestUnknownB0 { get; set; }

    /// <summary>
    /// m_skills is five u32 at net record +0x74..+0x84 in both builds (1.00
    /// reader 0x8bda40, 2.12 0x70e914): 160 skill bits. 1.00's table uses ids
    /// below 98, 2.12's up to 159.
    /// </summary>
    public const int SkillWords = 5;
    public const int ItemWords = 24;
    public const int SkillCount = SkillWords * 32;
    public const byte FlagFactionChanged = 0x08;

    /// <summary>
    /// Reads the body after the envelope: name, faction, head, voice,
    /// universe, the NpId (1.00 marshal 0x8c399c), and in 2.12 a trailing
    /// bool (0x7101e4) [C].
    /// </summary>
    public static MagCharacter ReadCreateRequest(ZipperBitReader reader, MagClientVersion version)
    {
        MagCharacter character = new()
        {
            Name = ReadShortString(reader),
            Faction = (sbyte)reader.ReadU8(),
            HeadId = reader.ReadU8(),
            VoiceId = reader.ReadU8(),
            UniverseId = reader.ReadU32(),
        };
        character.NpId = reader.ReadBytes(36);
        if (version.Format != MagWireFormat.Retail100 && reader.BitsLeft >= 1)
            character.CreateRequestUnknownB0 = reader.ReadBool();
        // The emulator's create (0x8a4e14) sets the faction through SetFaction, which records it in the history.
        character.FactionHistory = FactionBit(character.Faction);
        return character;
    }

    /// <summary>
    /// MagNetCharacterRecord in the order mage_g 0x8bda40 reads it (2.12:
    /// 0x70e914, the same through +0x228 and then <see cref="Unknown22A"/> and
    /// <see cref="Unknown238"/>). The notes give the net record offset;
    /// FromRecord (0x8bd7f8) fills each from the zServer record. Leadership,
    /// play time and nominations stay zero.
    /// </summary>
    public void WriteRecord(ZipperBitWriter writer, MagClientVersion version)
    {
        WriteShortString(writer, Name);   // +0x1b0 m_accountName, max 32
        writer.WriteU32(Id);              // +0x1d0 m_accountID
        writer.WriteU8((byte)Faction);    // +0x04 m_faction
        writer.WriteU8(SkillSpecialty);   // +0x08 m_skillSpecialty
        writer.WriteU8(Level);            // +0x0c m_level
        writer.WriteU32(Experience);      // +0x10 m_experience
        foreach (uint word in Fit(AvailableItems, ItemWords))   // +0x14..+0x70 m_availableItems
            writer.WriteU32(word);
        foreach (uint word in Fit(Skills, SkillWords))          // +0x74..+0x84 m_skills
            writer.WriteU32(word);
        writer.WriteU8(SkillPoints);      // +0x88 m_skillPoints
        foreach (MagLoadout loadout in Loadouts)
            loadout.Write(writer);
        writer.WriteU8(0);                // +0x1a4 m_leadershipLevel
        writer.WriteU32(0);               // +0x1a8 m_leadershipPoints
        writer.WriteU32(0);               // +0x1ac m_timePlayedSec
        writer.WriteU32(0);               // +0x1d4 (zServer record +0x1b8), not in the Dump
        writer.WriteU8(HeadId);           // +0x1d8 m_headID
        writer.WriteU8(VoiceId);          // +0x1dc m_voiceID
        writer.WriteU8(0);                // +0x1e0 m_leadershipNominations
        writer.WriteU8(0);                // +0x1e4 m_matchesSinceLastCommand
        writer.WriteU8(RespecCount);      // +0x1e8 m_respecCount
        writer.WriteU32(RespecPoints);    // +0x1ec m_respecPoints
        writer.WriteU32(UniverseId);      // +0x1f0 m_universeID
        writer.WriteU8(FactionHistory);   // +0x1f4 m_factionHistory
        writer.WriteBool(FactionReSpecAvailable); // +0x1f8 m_factionReSpecAvailable
        writer.WriteU8(LastUsedLoadout);  // +0x1fc m_lastUsedLoadout
        foreach (byte b in NpId)          // +0x200 m_npId, 36 bytes
            writer.WriteU8(b);
        writer.WriteU8(VeteranLevel);     // +0x224 m_veteranLevel
        writer.WriteU8(Flags);            // +0x228 m_flags
        if (version.Format == MagWireFormat.Retail100)
            return;
        foreach (ushort value in Fit(Unknown22A, 6))   // 2.12 +0x22a..+0x234
            writer.WriteU16(value);
        foreach (uint value in Fit(Unknown238, 5))     // 2.12 +0x238..+0x248
            writer.WriteU32(value);
    }

    /// <summary>
    /// The character-select entry (MagNetCharacterRecordSummary, read by
    /// mage_g 0x8bd468). The server builds it from its record in two steps
    /// (zServer record → zServer summary at 0x8a8afc, zServer summary → net
    /// summary at 0x8be47c); the field notes give the zServer record offset
    /// each value comes from. 2.12's list is of slots, and its summary
    /// (0x70e278) ends with the slot's two bools (<see cref="MagCharacterSlot"/>).
    /// </summary>
    public void WriteSummary(ZipperBitWriter writer, MagClientVersion version, MagCharacterSlot? slot = null)
    {
        WriteShortString(writer, Name);   // record +0x04, max 32
        writer.WriteU32(Id);              // record +0x24
        writer.WriteU8((byte)Faction);    // record +0x2c, read signed like the full record's faction
        writer.WriteU8(SkillSpecialty);   // computed by 0x8a2d44
        writer.WriteU8(Level);            // record +0x3c
        SelectedLoadout.Write(writer);    // the selected loadout (record +0x1d0 indexes the five)
        writer.WriteU32(UniverseId);      // record +0x1c4
        writer.WriteU8(FactionHistory);   // record +0x1c8
        writer.WriteBool(FactionReSpecAvailable); // record +0x1cc
        writer.WriteU8(HeadId);           // record +0x30
        writer.WriteU8(VoiceId);          // record +0x34
        writer.WriteU8(VeteranLevel);     // record +0x1f8
        if (version.Format == MagWireFormat.Retail100)
            return;
        slot ??= new MagCharacterSlot(this);
        writer.WriteBool(slot.Locked);    // summary +0x80
        writer.WriteBool(slot.Unknown81); // summary +0x81
    }

    /// <summary>
    /// Gives a soldier with no loadouts their PMC's default kits; true if it
    /// did. The client counts a soldier's loadouts from this record as it
    /// loads it (0x6c12c sets the limit, 5, then 0x6c194 and on add each in
    /// turn until one has no primary), and the deploy menu's wrap-around is
    /// that count (ArmoryNumLoadouts, read as kitSelectionMax). At 0 the menu
    /// never wraps and runs off its five names (live, 2026-11-06) [C]. The
    /// wiki gives every soldier five (Classes); patch 1.0x notes change
    /// "Loadout 3", one of the three default kits.
    /// </summary>
    public bool TakeDefaultLoadouts(IReadOnlyList<MagLoadout> kits)
    {
        if (kits.Count == 0 || Loadouts.Any(l => l.Items.Any(item => item != MagLoadout.Empty)))
            return false;
        for (int i = 0; i < Loadouts.Length && i < kits.Count; i++)
            Loadouts[i] = kits[i];
        return true;
    }

    private MagLoadout SelectedLoadout => Loadouts[LastUsedLoadout < Loadouts.Length ? LastUsedLoadout : 0];

    public bool HasSkill(int skill) => (Fit(Skills, SkillWords)[skill >> 5] & (1u << (skill & 31))) != 0;

    public int SkillsPurchased => Fit(Skills, SkillWords).Sum(word => System.Numerics.BitOperations.PopCount(word));

    /// <summary>
    /// CanPurchaseSkill then PurchaseSkill. With the skill table: a known id
    /// not bought yet, available to the PMC and its cost in points; then the
    /// table's own prerequisites:
    /// <list type="bullet">
    /// <item>1.00 (0x8a374c): for tiers 2..5, enough points already spent in
    /// the tier below of the same category.</item>
    /// <item>2.x skill tree (0x6fa084): the parent skill bought, a
    /// levelPrerequisite of 2 or more reached, and the prerequisite skill
    /// bought; no tiers.</item>
    /// </list>
    /// Each rule applies when the table has its field, so one check serves
    /// both. Buying (1.00 0x8a31fc, 2.12 0x6fab00) spends the cost, sets the
    /// skill bit and the bits of the items it unlocks. Without the table any
    /// id costs the flat setting [C].
    /// </summary>
    public bool TryPurchaseSkill(int skill, MagRules rules, out string reason)
    {
        Skills = Fit(Skills, SkillWords);
        AvailableItems = Fit(AvailableItems, ItemWords);
        MagSkillTable? table = rules.Table;
        MagSkill? entry = table?.GetSkill(skill);
        int cost = rules.SkillCost(skill);
        if (skill is < 0 or >= SkillCount || (table is not null && entry is null))
            reason = "invalid skill id";
        else if (HasSkill(skill))
            reason = "skill already purchased";
        else if (entry is not null && !MagSkillTable.IsAvailable(entry, Faction))
            reason = $"skill is not available for faction {Faction}";
        else if (SkillPoints < cost)
            reason = $"not enough skill points ({SkillPoints}, needs {cost})";
        else if (entry is not null && PreviousTierPoints(table!, entry) is var (spent, needed) && spent < needed)
            reason = $"insufficient previous tier points (tier {entry.Tier} needs {needed}, {spent} spent)";
        else if (entry?.Parent is { } parent && !HasSkill(parent))
            reason = $"parent skill {parent} not purchased";
        else if (entry?.LevelPrerequisite is >= 2 and var level && Level < level)
            reason = $"level {level} required (is {Level})";
        else if (entry?.Prerequisite is { } prerequisite && !HasSkill(prerequisite))
            reason = $"prerequisite skill {prerequisite} not purchased";
        else
        {
            Purchase(skill, cost, entry);
            UpdateSpecialty(rules);
            reason = "";
            return true;
        }
        return false;
    }

    /// <summary>
    /// The free skills (<see cref="MagSkill.IsStarting"/>: 1.00's tier 0
    /// skills, 2.x's skillLevel 0 SKILL_ID_BasicDefault; Recruit, with the
    /// basic kit) the PMC has, bought at creation (0x8a4ee4) and after each respec. Only
    /// the missing ones are bought, so soldiers saved before the table existed
    /// get them too. Returns whether anything was bought.
    /// </summary>
    public bool GrantStartingSkills(MagRules rules)
    {
        if (rules.Table is not { } table)
            return false;
        Skills = Fit(Skills, SkillWords);
        AvailableItems = Fit(AvailableItems, ItemWords);
        bool granted = false;
        foreach (MagSkill entry in table.Skills)
        {
            if (entry.IsStarting && entry.Id < SkillCount && MagSkillTable.IsAvailable(entry, Faction) && !HasSkill(entry.Id))
            {
                Purchase(entry.Id, entry.Cost, entry);
                granted = true;
            }
        }
        if (granted)
            UpdateSpecialty(rules);
        return granted;
    }

    /// <summary>
    /// PerformSkillReSpec (0x8a6680): the requirement for the current respec
    /// count (0x8b2140) in respec points; every skill is refunded, respecCount
    /// + 1, the points are spent and the free skills bought again [C].
    /// </summary>
    public bool TrySkillReSpec(MagRules rules, out string reason)
    {
        if (rules.ReSpecCost(RespecCount) is not { } cost)
        {
            reason = $"no re-spec requirement for count {RespecCount}";
            return false;
        }
        if (RespecPoints < cost)
        {
            reason = $"insufficient re-spec points ({RespecPoints}, needs {cost})";
            return false;
        }
        RefundSkills(rules);
        RespecCount = (byte)Math.Min(RespecCount + 1, byte.MaxValue);
        RespecPoints -= cost;
        GrantStartingSkills(rules);
        reason = "";
        return true;
    }

    /// <summary>
    /// PerformFactionReSpec (0x8a5954): refuses the faction the soldier is
    /// already in; otherwise refunds every skill (and its gear) in the old
    /// PMC, switches PMC (adding it to the history), sets
    /// <see cref="FlagFactionChanged"/> and buys the new PMC's free skills [C].
    /// </summary>
    public bool TryFactionReSpec(sbyte faction, MagRules rules, out string reason)
    {
        if (faction is < 0 or > 2)
            reason = $"no such faction {faction}";
        else if (faction == Faction)
            reason = "already in that faction";
        else
        {
            RefundSkills(rules);
            SetFaction(faction);
            Flags |= FlagFactionChanged;
            GrantStartingSkills(rules);
            reason = "";
            return true;
        }
        return false;
    }

    /// <summary>
    /// PerformVeteranReSpec (0x8a51c4): only at the maximum level
    /// (CheckVeteranReSpecEligibility). Skills are refunded, the flag is set
    /// if the PMC changes, the PMC is set, veteranLevel + 1; level 1 with no
    /// experience, skill points or respec count, then AwardLevel(1) and the
    /// free skills [C]. <see cref="MagSettings.StartingSkillPoints"/> is added
    /// as at creation.
    /// </summary>
    public bool TryVeteranReSpec(sbyte faction, MagRules rules, out string reason)
    {
        if (faction is < 0 or > 2)
        {
            reason = $"no such faction {faction}";
            return false;
        }
        if (Level < rules.MaxLevel)
        {
            reason = $"not max level ({Level} of {rules.MaxLevel})";
            return false;
        }
        RefundSkills(rules);
        if (faction != Faction)
            Flags |= FlagFactionChanged;
        SetFaction(faction);
        VeteranLevel = (byte)Math.Min(VeteranLevel + 1, byte.MaxValue);
        Level = 1;
        Experience = rules.Table?.ExperienceForLevel(1) ?? 0;
        RespecCount = 0;
        SkillPoints = rules.StartingSkillPoints;
        GrantStartingSkills(rules);
        reason = "";
        return true;
    }

    /// <summary>
    /// Takes the soldier up to <paramref name="level"/> the way the client
    /// emulator's create does for StartingLevel (0x8a4e14): AwardLevel
    /// (0x8a46bc) for each level gained sets the level and adds that level's
    /// skill points (clamped to a byte, 0x8a82e8), and the experience becomes
    /// the table's value for the level. Returns whether anything changed; a
    /// soldier already at or above it is left as it is. AwardLevel also
    /// recomputes the leadership level from leadership points, which this
    /// server keeps at zero [C].
    /// </summary>
    public bool RaiseToLevel(int level, MagRules rules)
    {
        level = Math.Clamp(level, 1, Math.Max(1, rules.MaxLevel));
        if (Level >= level)
            return false;
        int points = SkillPoints;
        for (int next = Level + 1; next <= level; next++)
            points += rules.SkillPointsGained(next);
        SkillPoints = (byte)Math.Min(points, byte.MaxValue);
        Level = (byte)level;
        Experience = Math.Max(Experience, rules.ExperienceForLevel(level));
        return true;
    }

    /// <summary>
    /// Career stats by game type (m_gameType, e.g. GameType64), each by
    /// CharacterStatType name (kCharStat without the prefix), kept as the
    /// build's stat table says (<see cref="MagStatTable.UpdateOf"/>): summed
    /// over the games played, or the best of them. Stats the server works out
    /// (TotalExperience is <see cref="Experience"/>, ratios) aren't kept.
    /// </summary>
    public Dictionary<string, Dictionary<string, long>> CareerStats { get; set; } = [];

    /// <summary>Adds one game's stats to the career of its game type, each as <paramref name="stats"/>' table says.</summary>
    public void RecordGame(string gameType, IReadOnlyDictionary<string, long> game, MagStatTable stats)
    {
        if (!CareerStats.TryGetValue(gameType, out Dictionary<string, long>? career))
            CareerStats[gameType] = career = [];
        foreach ((string name, long value) in game)
        {
            switch (stats.UpdateOf(name))
            {
                case MagStatTable.Add:
                    career[name] = career.GetValueOrDefault(name) + value;
                    break;
                case MagStatTable.Max:
                    career[name] = Math.Max(career.GetValueOrDefault(name), value);
                    break;
            }
        }
    }

    /// <summary>A career stat over all game types, kept as <paramref name="stats"/>' table says.</summary>
    public long CareerStat(string name, MagStatTable stats) => stats.UpdateOf(name) == MagStatTable.Max
        ? CareerStats.Values.Select(c => c.GetValueOrDefault(name)).DefaultIfEmpty().Max()
        : CareerStats.Values.Sum(c => c.GetValueOrDefault(name));

    /// <summary>
    /// Adds experience earned in battle and takes the soldier up every level
    /// whose experience it now reaches, as <see cref="RaiseToLevel"/> does
    /// (each level's skill points). Returns the levels gained.
    /// </summary>
    public int AddExperience(uint experience, MagRules rules)
    {
        Experience = (uint)Math.Min((ulong)Experience + experience, uint.MaxValue);
        int level = Level;
        while (level < rules.MaxLevel && Experience >= rules.ExperienceForLevel(level + 1))
            level++;
        int gained = level - Level;
        RaiseToLevel(level, rules);
        return gained;
    }

    /// <summary>
    /// RecvLoadoutDeleteRequest (0x91a6d4): an index past the fifth fails;
    /// otherwise the later loadouts move up one and the last is cleared [C].
    /// </summary>
    public bool TryDeleteLoadout(int index)
    {
        if (index is < 0 or > 4 || Loadouts.Length != 5)
            return false;
        for (int i = index; i < Loadouts.Length - 1; i++)
            Loadouts[i] = Loadouts[i + 1];
        Loadouts[^1] = new MagLoadout();
        return true;
    }

    /// <summary>
    /// Un-purchases every skill (0x8a30c8 per skill): its cost comes back, its
    /// bit and its gear's bits go. Without the table the cost is the flat
    /// setting and all gear goes.
    /// </summary>
    private void RefundSkills(MagRules rules)
    {
        Skills = Fit(Skills, SkillWords);
        AvailableItems = Fit(AvailableItems, ItemWords);
        int refund = 0;
        for (int skill = 0; skill < SkillCount; skill++)
        {
            if (!HasSkill(skill))
                continue;
            refund += rules.SkillCost(skill);
            if (rules.Table?.GetSkill(skill) is { } entry)
                SetItems(entry, set: false);
        }
        SkillPoints = (byte)Math.Min(SkillPoints + refund, byte.MaxValue);
        Skills = new uint[SkillWords];
        if (rules.Table is null)
            AvailableItems = new uint[ItemWords];
        UpdateSpecialty(rules);
    }

    private void Purchase(int skill, int cost, MagSkill? entry)
    {
        SkillPoints = (byte)Math.Max(SkillPoints - cost, 0);
        Skills[skill >> 5] |= 1u << (skill & 31);
        if (entry is not null)
            SetItems(entry, set: true);
    }

    /// <summary>The skill's items for the current PMC (0x8b20d4), as availableItems bits.</summary>
    private void SetItems(MagSkill entry, bool set)
    {
        foreach (MagSkillItem item in MagSkillTable.Items(entry, Faction))
        {
            if (item.Bit is < 0 or >= ItemWords * 32)
                continue;
            if (set)
                AvailableItems[item.Bit >> 5] |= 1u << (item.Bit & 31);
            else
                AvailableItems[item.Bit >> 5] &= ~(1u << (item.Bit & 31));
        }
    }

    /// <summary>
    /// The tier prerequisite of CanPurchaseSkill: the cost of the purchased
    /// skills of the same category one tier down, against what the tier needs.
    /// </summary>
    private (int Spent, int Needed) PreviousTierPoints(MagSkillTable table, MagSkill entry)
    {
        if (entry.Tier is not { } tier)
            return (0, 0);
        int needed = table.TierPrereqPoints(entry.Category, Faction, tier);
        if (needed <= 0)
            return (0, 0);
        int spent = table.Skills.Where(s => s.Id < SkillCount && HasSkill(s.Id) && s.Category == entry.Category && s.Tier == tier - 1).Sum(s => s.Cost);
        return (spent, needed);
    }

    private void UpdateSpecialty(MagRules rules)
    {
        if (rules.Table is { } table)
            SkillSpecialty = table.SkillSpecialty(id => id < SkillCount && HasSkill(id));
    }

    /// <summary>SetFaction (0x8a826c): the faction, and its bit in the history.</summary>
    private void SetFaction(sbyte faction)
    {
        Faction = faction;
        FactionHistory |= FactionBit(faction);
    }

    private static byte FactionBit(sbyte faction) => faction switch { 0 => 1, 1 => 2, 2 => 4, _ => 0 };

    /// <summary>A stored bit set at its wire length, whatever an older or hand-edited save holds.</summary>
    private static T[] Fit<T>(T[]? words, int length) =>
        words is { Length: var n } && n == length ? words : [.. (words ?? []).Concat(new T[length]).Take(length)];

    /// <summary>MAG's short strings: a u8 length, then the characters (reader 0x12e2fc).</summary>
    public static string ReadShortString(ZipperBitReader reader) =>
        Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadU8()));

    public static void WriteShortString(ZipperBitWriter writer, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 31)
            bytes = bytes[..31];
        writer.WriteU8((byte)bytes.Length);
        foreach (byte b in bytes)
            writer.WriteU8(b);
    }
}

/// <summary>
/// One loadout as mage_g reads it (0x4574c4, and the same body in
/// LoadoutUpdateRequest at 0x4578bc): a byte, then 22 slots, each a present
/// bit followed by a 12-bit item id when set. An empty slot is 0xFFFF in
/// the client.
/// </summary>
public sealed class MagLoadout
{
    public const int Slots = 22;
    public const ushort Empty = 0xFFFF;

    public byte Header { get; set; }
    public ushort[] Items { get; set; } = Enumerable.Repeat(Empty, Slots).ToArray();

    public static MagLoadout Read(ZipperBitReader reader)
    {
        MagLoadout loadout = new() { Header = reader.ReadU8() };
        for (int i = 0; i < Slots; i++)
            loadout.Items[i] = reader.ReadBool() ? (ushort)reader.ReadBits(12) : Empty;
        return loadout;
    }

    public void Write(ZipperBitWriter writer)
    {
        writer.WriteU8(Header);
        foreach (ushort item in Items)
        {
            bool present = item != Empty;
            writer.WriteBool(present);
            if (present)
                writer.WriteBits(item, 12);
        }
    }
}

/// <summary>
/// One character slot of 2.12's character list. The 2.12 client keeps the
/// list's entries as its slots (0x6a288: NumCharacterSlots = the entry
/// count, app +0x7a5c; each entry a summary copied to app +0x73bc, 0x84
/// bytes) [C]. A slot whose summary has character id 0 is empty
/// (CharacterListExists 0x8bcac tests id != 0), and one whose +0x80 bool is
/// set is locked (IsSlotUnlocked 0x60a18 is true only for +0x80 == 0, and
/// for indexes below the count) [C]: the client offers to buy it on the
/// PlayStation Store ("Purchase Character Slot A..C"). So a list with no
/// entries has no slot to create a soldier in.
/// </summary>
/// <param name="Character">The soldier in the slot, or null for an empty one.</param>
/// <param name="Locked">Summary +0x80: the slot isn't bought.</param>
/// <param name="Unknown81">Summary +0x81; meaning unknown (the slot subscription's expiry is a candidate [guess]).</param>
public sealed record MagCharacterSlot(MagCharacter? Character, bool Locked = false, bool Unknown81 = false)
{
    /// <summary>The summary an empty slot carries: no name, character id 0.</summary>
    private static readonly MagCharacter Empty = new() { Id = 0 };

    public void Write(ZipperBitWriter writer, MagClientVersion version) =>
        (Character ?? Empty).WriteSummary(writer, version, this);
}
