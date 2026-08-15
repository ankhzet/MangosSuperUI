namespace MangosSuperUI.Mcp.Tools;

internal static class WoWLookups
{
    public static string RaceName(int race) => race switch
    {
        1 => "Human",
        2 => "Orc",
        3 => "Dwarf",
        4 => "Night Elf",
        5 => "Undead",
        6 => "Tauren",
        7 => "Gnome",
        8 => "Troll",
        _ => $"Unknown({race})"
    };

    public static string ClassName(int classId) => classId switch
    {
        1 => "Warrior",
        2 => "Paladin",
        3 => "Hunter",
        4 => "Rogue",
        5 => "Priest",
        7 => "Shaman",
        8 => "Mage",
        9 => "Warlock",
        11 => "Druid",
        _ => $"Unknown({classId})"
    };

    public static string GmLevelName(int level) => level switch
    {
        0 => "Player",
        1 => "Moderator",
        2 => "Ticket Master",
        3 => "Game Master",
        4 => "Basic Admin",
        5 => "Developer",
        6 => "Administrator",
        7 => "Console",
        _ => $"Unknown({level})"
    };

    public static string FormatPlaytime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
