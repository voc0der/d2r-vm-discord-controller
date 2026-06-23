using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace D2RAgent.Tests;

public sealed class D2RScreenSnippetAssetTests
{
    public static TheoryData<string, string, int, int, int, int> SnippetSources => new()
    {
        { "character_play_button_text.png", "char_screen_act1.png", 545, 683, 624, 699 },
        { "character_lobby_button_text.png", "char_screen_act1.png", 749, 683, 838, 700 },
        { "character_online_tab_text.png", "char_screen_act1.png", 1132, 31, 1210, 48 },
        { "character_offline_tab_text.png", "character_screen_but_offline.png", 1255, 31, 1334, 49 },
        { "lobby_create_game_tab_text.png", "lobby_create_game_screen.png", 846, 49, 961, 66 },
        { "lobby_join_game_tab_text.png", "lobby_join_game_screen.png", 973, 49, 1085, 66 },
        { "create_game_button_text.png", "lobby_create_game_screen.png", 991, 466, 1115, 489 },
        { "join_game_button_text.png", "lobby_join_game_screen.png", 986, 466, 1098, 489 },
        { "character_offline_empty_panel.png", "character_screen_but_offline.png", 1070, 133, 1340, 579 },
        { "modern_health_globe.png", "just_landed_in_game_checkforhealthandmanaglobes.png", 319, 652, 400, 742 },
        { "modern_mana_globe.png", "just_landed_in_game_checkforhealthandmanaglobes.png", 967, 652, 1047, 742 }
    };

    [Theory]
    [MemberData(nameof(SnippetSources))]
    public void SnippetMatchesItsSourceCrop(
        string snippetFileName,
        string sourceFileName,
        int left,
        int top,
        int right,
        int bottom)
    {
        using var snippet = Image.Load<Rgba32>(SnippetPath(snippetFileName));
        using var source = Image.Load<Rgba32>(SourcePath(sourceFileName));

        var expectedWidth = right - left;
        var expectedHeight = bottom - top;
        Assert.Equal(expectedWidth, snippet.Width);
        Assert.Equal(expectedHeight, snippet.Height);

        for (var y = 0; y < expectedHeight; y++)
        {
            for (var x = 0; x < expectedWidth; x++)
            {
                Assert.Equal(source[left + x, top + y], snippet[x, y]);
            }
        }
    }

    private static string SnippetPath(string fileName)
    {
        return Path.Combine(FindAssetsDirectory(), "snippets", fileName);
    }

    private static string SourcePath(string fileName)
    {
        return Path.Combine(FindAssetsDirectory(), fileName);
    }

    private static string FindAssetsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "D2ROps.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate repo root (D2ROps.sln) from test base directory.");
        }

        return Path.Combine(directory.FullName, "docs", "runbooks", "assets", "d2r-ui", "1366x768");
    }
}
