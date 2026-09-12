namespace D2RHost;

/// <summary>
/// The optional head start a follow-auto run gives the leader before the fleet walks into a new
/// game.
/// </summary>
/// <remarks>
/// <para>
/// D2R scales monster health and density to the number of players in the game. A full fleet joining
/// the instant the leader creates it means the leader is teleporting through Durance of Hate or the
/// Throne of Destruction at eight-player density on a character geared for far less. The bots are
/// not there to fight - they are there to be in the game - so the cheapest fix is to let the leader
/// arrive alone, make the run to the boss at one-player density, and have the fleet turn up
/// afterwards.
/// </para>
/// <para>
/// <see cref="DelaySeconds"/> is deliberately a constant rather than a setting. The useful value is
/// "long enough to teleport to the boss", which is the same half-minute whether the destination is
/// Mephisto or Baal, and a knob would only invite tuning a number that does not need tuning.
/// </para>
/// <para>
/// The hold is private mode's alone. Public mode's entire promise is that a real player can always
/// walk in, and it keeps that promise by reading the live player count off a client that is inside
/// the game; a deliberate half-minute with nobody in yet is a half-minute of no samples at all,
/// which reads as "waiting on the first live player count" rather than as the deliberate pause it
/// is. Park is not a live follow run to hold back in the first place.
/// </para>
/// </remarks>
internal static class FollowAutoJoinDelayPolicy
{
    /// <summary>How long the fleet waits out before entering a new game, when the hold is armed.</summary>
    public const int DelaySeconds = 30;

    /// <summary>
    /// Whether this join scan is the one the hold applies to: the fleet is arming the delay, is in
    /// private mode, and is about to enter a game it has nobody in yet.
    /// </summary>
    /// <remarks>
    /// The last condition is what keeps the hold from punishing a straggler. A bot rejoining after
    /// a wedge, a restart, or an isolated-vantage resync joins a game the rest of the fleet is
    /// already sitting in - the density is already raised and the leader is already past the run
    /// that needed covering, so making that one client wait another half-minute protects nobody and
    /// only lengthens the window where the fleet is short a vantage.
    /// </remarks>
    /// <param name="armed">Whether the monitor's delay toggle is currently on.</param>
    /// <param name="mode">The run's party mode; only <see cref="FollowAutoPartyMode.Private"/> holds.</param>
    /// <param name="fleetClientsInGame">
    /// Fleet clients already inside the game being joined. Zero means this is a fresh entry.
    /// </param>
    public static bool ShouldHoldBeforeJoin(bool armed, FollowAutoPartyMode mode, int fleetClientsInGame)
    {
        return armed
            && mode == FollowAutoPartyMode.Private
            && fleetClientsInGame <= 0;
    }
}
