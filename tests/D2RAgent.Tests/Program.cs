using D2RAgent;

var failures = new List<string>();

Check(
    "title-bearing character select is recognized from Play/Lobby buttons",
    D2RScreenClassifier.IsCharacterButtonRegion(Stats(76.7, 37.4, 0.09, 0.87, 0.11))
        && D2RScreenClassifier.IsCharacterButtonRegion(Stats(77.8, 40.9, 0.10, 0.85, 0.12)));

Check(
    "title-bearing character select is recognized from left menu",
    D2RScreenClassifier.IsCharacterMenuReady(
        Stats(60.9, 58.2, 0.15, 0.07, 0.45, orange: 0.11, red: 0.22),
        Stats(63.5, 39.1, 0.08, 0.72, 0.18),
        Stats(61.5, 47.2, 0.11, 0.60, 0.28)));

var characterAct5CreateTab = Stats(55.3, 29.0, 0.00, 0.70, 0.30);
var characterAct5JoinTab = Stats(51.9, 25.4, 0.00, 0.80, 0.19);
Check(
    "Act 5 character select tab sample alone still looks lobby-like",
    D2RScreenClassifier.IsLobbyTabRegion(characterAct5CreateTab)
        && D2RScreenClassifier.IsLobbyTabRegion(characterAct5JoinTab));

Check(
    "Act 5 character select is rejected as lobby when character anchors are present",
    !D2RScreenClassifier.IsLobbyTabReady(characterAct5CreateTab, characterButtonPairReady: true, characterMenuReady: true)
        && !D2RScreenClassifier.IsLobbyTabReady(characterAct5JoinTab, characterButtonPairReady: true, characterMenuReady: true));

Check(
    "Create Game lobby tab is accepted when character anchors are absent",
    D2RScreenClassifier.IsLobbyTabReady(
        Stats(34.8, 21.5, 0.00, 0.53, 0.46),
        characterButtonPairReady: false,
        characterMenuReady: false));

Check(
    "Join Game lobby tab is accepted when character anchors are absent",
    D2RScreenClassifier.IsLobbyTabReady(
        Stats(34.5, 23.5, 0.01, 0.51, 0.48),
        characterButtonPairReady: false,
        characterMenuReady: false));

Check(
    "inactive lobby tabs are not treated as the active tab",
    !D2RScreenClassifier.IsLobbyTabRegion(Stats(22.7, 11.9, 0.00, 0.05, 0.95))
        && !D2RScreenClassifier.IsLobbyTabRegion(Stats(25.4, 15.6, 0.00, 0.11, 0.89)));

if (failures.Count > 0)
{
    Console.Error.WriteLine("D2R agent regression tests failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("D2R agent regression tests passed.");
return 0;

void Check(string name, bool condition)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

static ScreenRegionStats Stats(
    double average,
    double stdDev,
    double bright,
    double grey,
    double dark,
    double orange = 0,
    double red = 0,
    double blue = 0)
{
    return new ScreenRegionStats(
        average,
        stdDev,
        bright,
        grey,
        dark,
        orange,
        red,
        blue,
        Samples: 289);
}
