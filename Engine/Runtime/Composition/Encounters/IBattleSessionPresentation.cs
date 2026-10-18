namespace PokerEngine
{
    /// <summary>
    /// Frontend boundary for a battle session. Implementations may display resolved snapshots
    /// immediately or play them over time; neither path is responsible for advancing rules.
    /// </summary>
    public interface IBattleSessionPresentation
    {
        /// <summary>True while any local presentation (including opening cards) must gate another command.</summary>
        bool IsPresenting { get; }
        /// <summary>Binds the initial snapshot; a started session may be attached without executing Start again.</summary>
        void Initialize(CardDuelState initialState);
        /// <summary>Consumes an accepted, already committed batch. Completion only advances the frontend's clock.</summary>
        void Present(BattleSessionResult batch, bool animate);
        /// <summary>Cancels pending playback and shows this snapshot without replaying or executing rules.</summary>
        void Synchronize(CardDuelState state);
    }
}
