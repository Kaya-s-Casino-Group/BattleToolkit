namespace PokerEngine
{
    public static class HoldemZoneKinds
    {
        public const string HoleCards = "holdem.hole", Board = "holdem.board", Burn = "holdem.burn";
    }
    public static class HoldemTiming
    {
        public const string BeforeHand = "holdem.before-hand", BeforeBettingAction = "holdem.before-action",
            AfterBettingAction = "holdem.after-action", AfterBoardDealt = "holdem.after-board",
            BeforeShowdown = "holdem.before-showdown", AfterHandSettled = "holdem.after-settlement";
    }
    public enum BettingAction { Fold = 1, Check, Call, RaiseTo }
    public static class HoldemActionKeys
    {
        public const string Fold = "holdem.fold", Check = "holdem.check", Call = "holdem.call", RaiseTo = "holdem.raise";
        public static string Key(BettingAction action)
        {
            switch(action) { case BettingAction.Fold: return Fold; case BettingAction.Check: return Check;
                case BettingAction.Call: return Call; case BettingAction.RaiseTo: return RaiseTo;
                default: throw new System.ArgumentException("Invalid betting action."); }
        }
    }
}
