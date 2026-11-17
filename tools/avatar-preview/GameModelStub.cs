namespace Belay.Players.Avatar
{
    /// <summary>
    /// The preview can't load the game's own models, but LoreLooks names them. These ids must match
    /// src/Belay/Players/Avatar/GameModelAvatar.cs.
    /// </summary>
    public sealed class GameModelAvatar
    {
        public const string GiantRoach = "roach", ZombieScientist = "zombie-scientist", ZombieSecurity = "zombie-security",
            Soach = "soach";
    }
}
