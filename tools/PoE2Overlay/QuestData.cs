using System.Collections.Generic;

namespace PoE2Overlay;

public enum StepType { Move, Kill, Talk, Waypoint, Interact, Pickup, Portal, Note }

public record GuideStep(StepType Type, string Text, bool IsOptional = false);

/// <summary>
/// Zone metadata and quest guide steps for PoE2 Acts 1–4.
/// Zone names match the "Generating level N area" areaId resolution.
/// </summary>
public static class QuestData
{
    // areaId → display name (from poe2-leveling-overlay areaIds.ts)
    static readonly Dictionary<string, string> AreaIds = new()
    {
        // Act 1
        ["g1_1"]    = "The Riverbank",
        ["g1_town"] = "Clearfell Encampment",
        ["g1_2"]    = "Clearfell",
        ["g1_3"]    = "The Mud Burrow",
        ["g1_4"]    = "The Grelwood",
        ["g1_5"]    = "The Red Vale",
        ["g1_6"]    = "The Grim Tangle",
        ["g1_7"]    = "Cemetery of the Eternals",
        ["g1_8"]    = "The Mausoleum of the Praetor",
        ["g1_9"]    = "Tomb of the Consort",
        ["g1_11"]   = "The Hunting Grounds",
        ["g1_12"]   = "Freythorn",
        ["g1_13_1"] = "Ogham Farmlands",
        ["g1_13_2"] = "Ogham Village",
        ["g1_14"]   = "The Manor Ramparts",
        ["g1_15"]   = "Ogham Manor",
        // Act 2
        ["g2_1"]    = "Vastiri Outskirts",
        ["g2_town"] = "The Ardura Caravan",
        ["g2_2"]    = "Traitor's Passage",
        ["g2_3"]    = "The Halani Gates",
        ["g2_3a"]   = "The Halani Gates",
        ["g2_4_1"]  = "Keth",
        ["g2_4_2"]  = "The Lost City",
        ["g2_4_3"]  = "Buried Shrines",
        ["g2_5_1"]  = "Mastodon Badlands",
        ["g2_5_2"]  = "The Bone Pits",
        ["g2_6"]    = "Valley of the Titans",
        ["g2_7"]    = "The Titan Grotto",
        ["g2_8"]    = "Deshar",
        ["g2_9_1"]  = "Path of Mourning",
        ["g2_9_2"]  = "The Spires of Deshar",
        ["g2_10_1"] = "Mawdun Quarry",
        ["g2_10_2"] = "Mawdun Mine",
        ["g2_12_1"] = "The Dreadnought",
        ["g2_13"]   = "Trial of the Sekhemas",
        // Act 3
        ["g3_1"]         = "Sandswept Marsh",
        ["g3_town"]      = "Ziggurat Encampment",
        ["g3_2_1"]       = "Infested Barrens",
        ["g3_2_2"]       = "The Matlan Waterways",
        ["g3_3"]         = "Jungle Ruins",
        ["g3_4"]         = "The Venom Crypts",
        ["g3_5"]         = "Chimeral Wetlands",
        ["g3_6_1"]       = "Jiquani's Machinarium",
        ["g3_6_2"]       = "Jiquani's Sanctum",
        ["g3_7"]         = "The Azak Bog",
        ["g3_8"]         = "The Drowned City",
        ["g3_9"]         = "The Molten Vault",
        ["g3_10_airlock"]= "Temple of Chaos",
        ["g3_11"]        = "Apex of Filth",
        ["g3_12"]        = "Temple of Kopec",
        ["g3_14"]        = "Utzaal",
        ["g3_16"]        = "Aggorat",
        ["g3_17"]        = "The Black Chambers",
        // Act 4
        ["g4_town"]  = "Kingsmarch",
        ["g4_1_1"]   = "Isle of Kin",
        ["g4_1_2"]   = "Volcanic Warrens",
        ["g4_2_1"]   = "Kedge Bay",
        ["g4_2_2"]   = "Journey's End",
        ["g4_3_1"]   = "Whakapanu Island",
        ["g4_3_2"]   = "Singing Caverns",
        ["g4_4_1"]   = "Eye of Hinekora",
        ["g4_4_2"]   = "Halls of the Dead",
        ["g4_4_3"]   = "Trial of the Ancestors",
        ["g4_5_1"]   = "Abandoned Prison",
        ["g4_5_2"]   = "Solitary Confinement",
        ["g4_7"]     = "Shrike Island",
        ["g4_8a"]    = "Arastas",
        ["g4_8b"]    = "Arastas",
        ["g4_10"]    = "The Excavation",
        ["g4_11_1a"] = "Ngakanu",
        ["g4_11_1b"] = "Ngakanu",
        ["g4_11_2"]  = "Heart of the Tribe",
        // Interludes
        ["p1_1"] = "Scorched Farmlands",
        ["p1_2"] = "Stones of Serle",
        ["p1_3"] = "The Blackwood",
        ["p1_4"] = "Holten",
        ["p1_5"] = "Wolvenhold",
        ["p1_6"] = "Holten Estate",
        ["p2_1"] = "The Khari Crossing",
        ["p2_2"] = "Pools of Khatal",
        ["p2_3"] = "Sel Khari Sanctuary",
        ["p2_5"] = "The Galai Gates",
        ["p2_6"] = "Qimah",
        ["p2_7"] = "Qimah Reservoir",
        ["p3_town"] = "The Glade",
        ["p3_1"] = "Ashen Forest",
        ["p3_2"] = "Kriar Village",
        ["p3_5"] = "Kriar Peaks",
    };

    // display name → act number
    static readonly Dictionary<string, int> ZoneActs = new()
    {
        // Act 1
        ["The Riverbank"]              = 1, ["Clearfell"]                   = 1,
        ["Clearfell Encampment"]       = 1, ["The Mud Burrow"]              = 1,
        ["The Grelwood"]               = 1, ["The Red Vale"]                = 1,
        ["The Grim Tangle"]            = 1, ["Cemetery of the Eternals"]    = 1,
        ["The Mausoleum of the Praetor"]= 1, ["Tomb of the Consort"]        = 1,
        ["The Hunting Grounds"]        = 1, ["Freythorn"]                   = 1,
        ["Ogham Farmlands"]            = 1, ["Ogham Village"]               = 1,
        ["The Manor Ramparts"]         = 1, ["Ogham Manor"]                 = 1,
        // Act 2
        ["Vastiri Outskirts"]          = 2, ["The Ardura Caravan"]          = 2,
        ["Traitor's Passage"]          = 2, ["The Halani Gates"]            = 2,
        ["Keth"]                       = 2, ["The Lost City"]               = 2,
        ["Buried Shrines"]             = 2, ["Mastodon Badlands"]           = 2,
        ["The Bone Pits"]              = 2, ["Valley of the Titans"]        = 2,
        ["The Titan Grotto"]           = 2, ["Deshar"]                      = 2,
        ["Path of Mourning"]           = 2, ["The Spires of Deshar"]        = 2,
        ["Mawdun Quarry"]              = 2, ["Mawdun Mine"]                 = 2,
        ["The Dreadnought"]            = 2, ["Trial of the Sekhemas"]       = 2,
        // Act 3
        ["Sandswept Marsh"]            = 3, ["Ziggurat Encampment"]         = 3,
        ["Infested Barrens"]           = 3, ["The Matlan Waterways"]        = 3,
        ["Jungle Ruins"]               = 3, ["The Venom Crypts"]            = 3,
        ["Chimeral Wetlands"]          = 3, ["Jiquani's Machinarium"]       = 3,
        ["Jiquani's Sanctum"]          = 3, ["The Azak Bog"]                = 3,
        ["The Drowned City"]           = 3, ["The Molten Vault"]            = 3,
        ["Temple of Chaos"]            = 3, ["Apex of Filth"]               = 3,
        ["Temple of Kopec"]            = 3, ["Utzaal"]                      = 3,
        ["Aggorat"]                    = 3, ["The Black Chambers"]          = 3,
        // Act 4
        ["Kingsmarch"]                 = 4, ["Isle of Kin"]                 = 4,
        ["Volcanic Warrens"]           = 4, ["Kedge Bay"]                   = 4,
        ["Journey's End"]              = 4, ["Whakapanu Island"]            = 4,
        ["Singing Caverns"]            = 4, ["Eye of Hinekora"]             = 4,
        ["Halls of the Dead"]          = 4, ["Trial of the Ancestors"]      = 4,
        ["Abandoned Prison"]           = 4, ["Solitary Confinement"]        = 4,
        ["Shrike Island"]              = 4, ["Arastas"]                     = 4,
        ["The Excavation"]             = 4, ["Ngakanu"]                     = 4,
        ["Heart of the Tribe"]         = 4,
    };

    static readonly Dictionary<string, List<GuideStep>> Steps = new()
    {
        // ──────────────────────────────────────────── ACT 1 ─────────────────
        ["The Riverbank"] = new()
        {
            new(StepType.Move, "Intro zone — head forward through the Riverbank."),
            new(StepType.Kill, "Kill Bloated Miller (miniboss blocking the path)."),
            new(StepType.Talk, "Talk to Renly after the Bloated Miller dies."),
        },
        ["Clearfell"] = new()
        {
            new(StepType.Kill, "SKILL POINTS: Kill Beira of the Rotten Pack. Drops Head of the Winter Wolf — needed to craft Runed Staff for Renly."),
            new(StepType.Move, "Find exit north to Clearfell Encampment."),
        },
        ["Clearfell Encampment"] = new()
        {
            new(StepType.Waypoint, "Activate the Clearfell Encampment Waypoint."),
            new(StepType.Talk,     "Talk to Renly (accept Secrets in the Dark), Una, and Finn for all available quests."),
            new(StepType.Portal,   "Exit east into The Grelwood."),
        },
        ["The Mud Burrow"] = new()
        {
            new(StepType.Move, "Small dungeon — find exit on the far side. No special objectives."),
        },
        ["The Grelwood"] = new()
        {
            new(StepType.Move, "Find BOTH exits: Red Vale (west/southwest) AND Grim Tangle (east). Grab both waypoints."),
            new(StepType.Note, "Tree of Souls is in the NE — return here after collecting all 3 Runes of Power."),
        },
        ["The Red Vale"] = new()
        {
            new(StepType.Waypoint, "Activate Red Vale Waypoint."),
            new(StepType.Interact, "Find and activate all 3 Obelisks of Rust (1st and 2nd in lower-east, 3rd in north)."),
            new(StepType.Kill,     "3rd Obelisk spawns The Rust King — kill him. Collect Rune of Power."),
            new(StepType.Note,     "After all 3 runes collected: return to Renly in Encampment, then use Tree of Souls in Grelwood (NE)."),
        },
        ["The Grim Tangle"] = new()
        {
            new(StepType.Waypoint, "Activate Grim Tangle Waypoint."),
            new(StepType.Move,     "Navigate toward Cemetery of the Eternals (follow The Mysterious Shade)."),
        },
        ["Cemetery of the Eternals"] = new()
        {
            new(StepType.Move, "Find Tomb of the Consort (north/northwest) AND Mausoleum of the Praetor (northeast)."),
            new(StepType.Kill, "After ring pickup in Mausoleum: return here to kill Lachlann of Endless Lament."),
        },
        ["Tomb of the Consort"] = new()
        {
            new(StepType.Kill, "Kill Asinia, Praetor Consort (required main quest boss — Sorrow Among Stones)."),
        },
        ["The Mausoleum of the Praetor"] = new()
        {
            new(StepType.Kill,   "Kill Draven, the Eternal Praetor (required main quest boss)."),
            new(StepType.Pickup, "Pick up Count Lachlann's Ring from the altar in the boss room."),
            new(StepType.Portal, "Use the checkpoint portal to exit quickly."),
        },
        ["The Hunting Grounds"] = new()
        {
            new(StepType.Kill, "SKILL POINTS (+2 SP): Kill The Crowbell — unique boss. Reward: 2 Passive Skill Points."),
            new(StepType.Move, "Find exit east to Freythorn."),
        },
        ["Freythorn"] = new()
        {
            new(StepType.Kill, "SKILL POINTS: Complete Ominous Altars encounter in Freythorn (summon & kill Ritual boss)."),
            new(StepType.Move, "Find exit to Ogham Farmlands (eastern/southeastern edge)."),
        },
        ["Ogham Farmlands"] = new()
        {
            new(StepType.Pickup, "SKILL POINTS: Find Una's Lute — usually in the CENTER-SOUTH of the zone. Look for glowing item on the ground."),
            new(StepType.Move,   "Continue north to Ogham Village."),
        },
        ["Ogham Village"] = new()
        {
            new(StepType.Kill,    "Kill The Executioner (Trail of Corruption quest, opposite end of village)."),
            new(StepType.Interact,"Free Leitis from her cage after killing The Executioner."),
            new(StepType.Waypoint,"Activate Ogham Village Waypoint."),
            new(StepType.Talk,    "Turn in The Lost Lute to Una. Talk to Leitis."),
            new(StepType.Portal,  "Exit north to The Manor Ramparts."),
        },
        ["The Manor Ramparts"] = new()
        {
            new(StepType.Move, "Navigate the ramparts to reach Ogham Manor entrance."),
        },
        ["Ogham Manor"] = new()
        {
            new(StepType.Waypoint, "Activate Ogham Manor Waypoint."),
            new(StepType.Kill,     "Floor 1: Kill Candlemass, the Living Rite at the Fallen Altar (gives Ascendancy trials access)."),
            new(StepType.Kill,     "ACT BOSS: Descend Floors 2-3, kill Count Geonor. Loot Smithing Tools."),
            new(StepType.Portal,   "Enter portal to Act 2. Return to Encampment to turn in Smithing Tools to Renly."),
        },

        // ──────────────────────────────────────────── ACT 2 ─────────────────
        ["Vastiri Outskirts"] = new()
        {
            new(StepType.Waypoint, "Activate Vastiri Outskirts Waypoint."),
            new(StepType.Talk,     "Talk to Shambrin — accept Earning Passage and all available quests."),
            new(StepType.Portal,   "Exit to Mawdun Quarry."),
        },
        ["Mawdun Quarry"] = new()
        {
            new(StepType.Kill, "Kill Rathbreaker (Earning Passage quest boss — large desert beast blocking the caravan)."),
            new(StepType.Move, "Continue into Mawdun Mine."),
        },
        ["Mawdun Mine"] = new()
        {
            new(StepType.Move, "Navigate through the mine tunnels to Traitor's Passage exit."),
        },
        ["Traitor's Passage"] = new()
        {
            new(StepType.Kill, "ASCENDANCY UNLOCK: Kill Balbala the Traitor. This unlocks Trial of the Sekhemas — REQUIRED for Ascendancy."),
            new(StepType.Move, "Continue to The Halani Gates."),
        },
        ["The Halani Gates"] = new()
        {
            new(StepType.Kill, "Kill Dreadnought Vanguard blocking the gates."),
            new(StepType.Move, "Proceed to Mastodon Badlands."),
        },
        ["Mastodon Badlands"] = new()
        {
            new(StepType.Move, "Navigate to The Bone Pits (southeast)."),
            new(StepType.Note, "Keth (The Lost City / Buried Shrines) is an optional side area off The Bone Pits.", IsOptional: true),
        },
        ["The Bone Pits"] = new()
        {
            new(StepType.Move, "Navigate through The Bone Pits toward Valley of the Titans."),
        },
        ["Keth"] = new()
        {
            new(StepType.Move, "Optional side area — Ancient Vows quest items and gems. Exit leads to The Lost City.", IsOptional: true),
        },
        ["The Lost City"] = new()
        {
            new(StepType.Move, "Navigate through ruins. Buried Shrines side area connects here.", IsOptional: true),
        },
        ["Valley of the Titans"] = new()
        {
            new(StepType.Move, "Navigate toward The Titan Grotto."),
        },
        ["The Titan Grotto"] = new()
        {
            new(StepType.Kill, "Kill the Titan boss (Ancient Vows main quest objective)."),
            new(StepType.Move, "Exit to Deshar."),
        },
        ["Deshar"] = new()
        {
            new(StepType.Waypoint, "Activate Deshar Waypoint."),
            new(StepType.Talk,     "Talk to NPCs in Deshar — pick up available quests."),
            new(StepType.Move,     "Find exit to The Ardura Caravan (Act 2 hub)."),
        },
        ["The Ardura Caravan"] = new()
        {
            new(StepType.Waypoint, "Activate The Ardura Caravan Waypoint (Act 2 hub)."),
            new(StepType.Talk,     "Talk to all Caravan NPCs — turn in Earning Passage, grab new quests."),
            new(StepType.Portal,   "Exit to Path of Mourning."),
        },
        ["Path of Mourning"] = new()
        {
            new(StepType.Move, "Navigate to The Spires of Deshar (northeast)."),
        },
        ["The Spires of Deshar"] = new()
        {
            new(StepType.Move, "Climb the spires — exit leads to The Dreadnought."),
        },
        ["The Dreadnought"] = new()
        {
            new(StepType.Kill, "ACT BOSS (0.5): Kill Jamanra the Abomination inside The Dreadnought."),
            new(StepType.Portal,"Enter portal to Act 3."),
        },
        ["Trial of the Sekhemas"] = new()
        {
            new(StepType.Note, "Ascendancy trial — unlocked by killing Balbala in Traitor's Passage. Complete to earn Ascendancy points."),
        },

        // ──────────────────────────────────────────── ACT 3 ─────────────────
        ["Sandswept Marsh"] = new()
        {
            new(StepType.Move, "Navigate northeast to Ziggurat Encampment (stone ziggurats visible above the tree line)."),
            new(StepType.Note, "Explorer camps in the marsh point to the next area when you talk to them (0.5 change)."),
        },
        ["Ziggurat Encampment"] = new()
        {
            new(StepType.Waypoint, "Activate Ziggurat Encampment Waypoint."),
            new(StepType.Talk,     "Talk to Alva Valai, Servi, and Oswald — pick up Legacy of the Vaal and all available quests."),
            new(StepType.Portal,   "Exit to Jungle Ruins."),
        },
        ["Jungle Ruins"] = new()
        {
            new(StepType.Move, "Navigate Jungle Ruins. Matlan Waterways entrance is here (0.5 change)."),
            new(StepType.Note, "Venom Crypts is a side area (Slithering Dead quest — optional). Infested Barrens is main path."),
        },
        ["The Venom Crypts"] = new()
        {
            new(StepType.Move, "Side dungeon — Slithering Dead quest (gem reward only). Optional.", IsOptional: true),
        },
        ["Infested Barrens"] = new()
        {
            new(StepType.Move, "Navigate to Chimeral Wetlands."),
        },
        ["The Azak Bog"] = new()
        {
            new(StepType.Kill, "OPTIONAL +30 SPIRIT: Kill Ignagduk, the Bog Witch. Reward: permanent +30 Spirit — very valuable.", IsOptional: true),
        },
        ["Chimeral Wetlands"] = new()
        {
            new(StepType.Kill, "ASCENDANCY UNLOCK: Kill Xyclucian, the Chimera. Drops Trial of Chaos key — REQUIRED for Ascendancy."),
            new(StepType.Move, "Continue to Jiquani's Machinarium."),
        },
        ["Jiquani's Machinarium"] = new()
        {
            new(StepType.Move, "Navigate the construct-filled ruins to Jiquani's Sanctum."),
        },
        ["Jiquani's Sanctum"] = new()
        {
            new(StepType.Kill, "Kill Jiquani boss (main quest — Victory Over Vaal objective)."),
            new(StepType.Move, "Exit to The Matlan Waterways."),
        },
        ["The Matlan Waterways"] = new()
        {
            new(StepType.Move, "Navigate waterways — Azak Bog side area connects here."),
            new(StepType.Move, "Find exit to The Drowned City."),
        },
        ["The Drowned City"] = new()
        {
            new(StepType.Move, "Navigate flooded ruins. The Molten Vault is an optional side area."),
            new(StepType.Move, "Find exit to Apex of Filth."),
        },
        ["The Molten Vault"] = new()
        {
            new(StepType.Move, "Optional side area — unique items and experience. No main quest objectives.", IsOptional: true),
        },
        ["Apex of Filth"] = new()
        {
            new(StepType.Move, "Navigate to Temple of Kopec."),
        },
        ["Temple of Kopec"] = new()
        {
            new(StepType.Move, "Navigate through Temple of Kopec to Utzaal."),
        },
        ["Utzaal"] = new()
        {
            new(StepType.Move, "Navigate to Aggorat."),
        },
        ["Aggorat"] = new()
        {
            new(StepType.Move, "Navigate to The Black Chambers."),
        },
        ["The Black Chambers"] = new()
        {
            new(StepType.Kill,   "ACT BOSS: Kill Doryani, Royal Thaumaturge."),
            new(StepType.Portal, "Enter portal to Act 4."),
        },
        ["Temple of Chaos"] = new()
        {
            new(StepType.Waypoint, "Waypoint available here."),
            new(StepType.Note,     "Ascendancy trial zone — complete Trial of Chaos for Ascendancy points."),
        },

        // ──────────────────────────────────────────── ACT 4 ─────────────────
        ["Kingsmarch"] = new()
        {
            new(StepType.Waypoint, "Activate Kingsmarch Waypoint."),
            new(StepType.Talk,     "Talk to ALL NPCs — Tujen (Dark Mists), Kaimana, Ange, Dannig, Makoru. Grab every quest."),
            new(StepType.Note,     "ACT 4 IS NON-LINEAR — 4 islands must be completed before Arastas unlocks. Recommended order: Whakapanu → Abandoned Prison → Isle of Kin → Shrike Island. Do Dark Mists (Kedge Bay) in parallel."),
        },
        ["Kedge Bay"] = new()
        {
            new(StepType.Move, "Dark Mists quest start — navigate to find the mist source."),
        },
        ["Journey's End"] = new()
        {
            new(StepType.Kill, "Kill the Dark Mists boss (Tujen's Dark Mists quest). Reward: +2 Passive Skill Points."),
        },
        ["Whakapanu Island"] = new()
        {
            new(StepType.Move, "Navigate Whakapanu Island."),
            new(StepType.Note, "Singing Caverns is the dungeon area on this island."),
        },
        ["Singing Caverns"] = new()
        {
            new(StepType.Kill, "Kill the Singing Caverns boss. Complete island objective."),
        },
        ["Isle of Kin"] = new()
        {
            new(StepType.Move, "Navigate Isle of Kin (Land of the Kin quest)."),
        },
        ["Volcanic Warrens"] = new()
        {
            new(StepType.Kill, "Kill the Volcanic Warrens boss. Complete isle objective."),
        },
        ["Abandoned Prison"] = new()
        {
            new(StepType.Move, "Navigate the prison to Solitary Confinement."),
        },
        ["Solitary Confinement"] = new()
        {
            new(StepType.Kill, "Kill the Prison Warden boss. Frees Matiki — allows Trial of Ancestors unlock."),
        },
        ["Shrike Island"] = new()
        {
            new(StepType.Move, "Navigate Shrike Island."),
        },
        ["Eye of Hinekora"] = new()
        {
            new(StepType.Kill, "Kill Hinekora's guardian. Reward: +5% Max Mana (permanent)."),
            new(StepType.Note, "ASCENDANCY: Trial of the Ancestors entrance is here — unlocked after freeing Matiki."),
        },
        ["Halls of the Dead"] = new()
        {
            new(StepType.Move, "Navigate Halls of the Dead."),
        },
        ["Trial of the Ancestors"] = new()
        {
            new(StepType.Kill, "SKILL POINTS (+2 SP): Complete Trial of the Ancestors. Reward: 2 Passive Skill Points."),
        },
        ["Arastas"] = new()
        {
            new(StepType.Note, "Unlocks after completing all 4 islands. Navigate Arastas ruins."),
            new(StepType.Move, "Find exit to The Excavation."),
        },
        ["The Excavation"] = new()
        {
            new(StepType.Move, "Navigate The Excavation to Ngakanu."),
        },
        ["Ngakanu"] = new()
        {
            new(StepType.Move, "Navigate Ngakanu to Heart of the Tribe."),
        },
        ["Heart of the Tribe"] = new()
        {
            new(StepType.Kill,   "ACT BOSS: Kill Tavakai, the Consumed."),
            new(StepType.Portal, "Act 4 Complete — enter portal to endgame."),
        },
    };

    public static string? ResolveAreaId(string areaId)
    {
        var id = areaId.TrimEnd('_');
        return AreaIds.TryGetValue(id, out var name) ? name : null;
    }

    public static int GetAct(string zoneName) =>
        ZoneActs.TryGetValue(zoneName, out var act) ? act : 0;

    public static List<GuideStep> GetSteps(string zoneName) =>
        Steps.TryGetValue(zoneName, out var s) ? s : _empty;

    static readonly List<GuideStep> _empty = new();
}
