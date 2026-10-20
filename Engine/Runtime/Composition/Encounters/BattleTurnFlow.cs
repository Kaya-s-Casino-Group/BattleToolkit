using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum BattlePlayerWindow { Draw = 1, Action, Ending, ForcedDiscard }
    public enum BattleDiscardKind { Defense = 1, BeforeDraw, Overflow, Effect }

    /// <summary>Opt-in turn flow. Old content keeps automatic refills. All limits are authority-owned.</summary>
    public sealed class BattleTurnFlowRules
    {
        public int MinFlushCards { get; }
        public int MinStraightCards { get; }
        public bool ManualDraw { get; }
        public bool OpeningDeal { get; }
        public int DrawCount { get; }
        public int DrawUses { get; }
        public int DrawWarningThreshold { get; }
        public int MinPlayCards { get; }
        public int ActionsPerTurn { get; }
        public bool EffectDrawOverflow { get; }
        public bool DiscardEndsTurn { get; }
        public bool DiscardCostsAction { get; }
        public bool EndDefense { get; }
        public bool OverflowTriggersDefense { get; }
        public KickerScoring KickerScoring { get; }
        public bool LayeredScoreRelics { get; }
        public BattleTurnFlowRules(bool manualDraw = false, bool openingDeal = false, int drawCount = 5, int drawUses = 1,
            int drawWarningThreshold = 3, int minPlayCards = 1, bool effectDrawOverflow = true, bool discardEndsTurn = true,
            bool discardCostsAction = true, bool endDefense = true, bool overflowTriggersDefense = false,
            KickerScoring kickerScoring = KickerScoring.None, bool layeredScoreRelics = false, int actionsPerTurn = 3, int minFlushCards = 5, int minStraightCards = 5)
        {
            if (minFlushCards < 3 || minFlushCards > 5 || minStraightCards < 3 || minStraightCards > 5 || actionsPerTurn < 1 || actionsPerTurn > 64 || drawCount < 0 || drawCount > 512 || drawUses < 0 || drawUses > 64 || drawWarningThreshold < 0 || drawWarningThreshold > 512 ||
                minPlayCards < 1 || minPlayCards > 10 || !Enum.IsDefined(typeof(KickerScoring), kickerScoring))
                throw new ArgumentException("Invalid configurable turn flow.");
            MinFlushCards = minFlushCards; MinStraightCards = minStraightCards; ActionsPerTurn = actionsPerTurn; ManualDraw = manualDraw; OpeningDeal = openingDeal; DrawCount = drawCount; DrawUses = drawUses;
            DrawWarningThreshold = drawWarningThreshold; MinPlayCards = minPlayCards; EffectDrawOverflow = effectDrawOverflow;
            DiscardEndsTurn = discardEndsTurn; DiscardCostsAction = discardCostsAction; EndDefense = endDefense;
            OverflowTriggersDefense = overflowTriggersDefense; KickerScoring = kickerScoring; LayeredScoreRelics = layeredScoreRelics;
        }
    }
    public sealed class BattlePlayerFlow
    {
        public BattlePlayerWindow Window { get; }
        public int DrawUsesRemaining { get; }
        public int ActionsRemaining { get; }
        public bool DefenseUsed { get; }
        public bool FinishAfterOverflow { get; }
        public BattlePlayerFlow(BattlePlayerWindow window, int draws, bool defenseUsed = false, bool finishAfterOverflow = false, int actionsRemaining = 3)
        { ActionsRemaining = actionsRemaining; Window = window; DrawUsesRemaining = draws; DefenseUsed = defenseUsed; FinishAfterOverflow = finishAfterOverflow; }
        internal BattlePlayerFlow With(BattlePlayerWindow window, bool? defenseUsed = null, bool? finish = null, int? actions = null) =>
            new BattlePlayerFlow(window, DrawUsesRemaining, defenseUsed ?? DefenseUsed, finish ?? FinishAfterOverflow, actions ?? ActionsRemaining);
    }
}
