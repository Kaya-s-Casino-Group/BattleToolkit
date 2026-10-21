using System;

namespace PokerEngine
{
    /// <summary>Frontend-independent session lifecycle. Rules remain owned by BattleSession.</summary>
    public sealed class BattleHost
    {
        private readonly Func<BattleSession> createSession;
        private bool changingSession;
        public IBattleSessionCommands Commands { get; private set; }
        public BattleSession Session => Commands?.Session;
        public BattleSessionPlayback Playback { get; private set; }
        public CardDuelState AuthorityState => Session?.State;
        /// <summary>Raised after Initialize completes and the new command port is available, before opening Start.</summary>
        public event Action SessionAttached;

        public BattleHost(Func<BattleSession> createSession)
        {
            this.createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        }

        /// <summary>Creates a fresh undealt session, binds its presentation, then submits the opening command.</summary>
        public BattleSessionResult StartBattle(IBattleSessionPresentation presentation, bool animate = true)
        {
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            BeginChange();
            try
            {
                var next = createSession() ?? throw new InvalidOperationException("The battle factory returned no session.");
                if (next.State.HasStarted) throw new InvalidOperationException("The battle factory must return an unstarted session.");
                Install(new BattleSessionCommands(next), presentation);
                return Playback.Start(animate);
            }
            finally { changingSession = false; }
        }

        /// <summary>Rebuilds from the current configuration rather than replaying the old opening snapshot.</summary>
        public BattleSessionResult RestartBattle(IBattleSessionPresentation presentation, bool animate = true) =>
            StartBattle(presentation, animate);

        /// <summary>Attaches any existing session without executing rules or creating another encounter.</summary>
        public void AttachSession(BattleSession session, IBattleSessionPresentation presentation)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            BeginChange();
            try { Install(new BattleSessionCommands(session), presentation); }
            finally { changingSession = false; }
        }

        /// <summary>Attaches an outer authority, optionally presenting its already-committed opening batch.</summary>
        public void AttachCommands(IBattleSessionCommands commands, IBattleSessionPresentation presentation,
            BattleSessionResult opening = null, bool animate = true)
        {
            if (commands == null || commands.Session == null) throw new ArgumentNullException(nameof(commands));
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            if (opening != null && (!opening.Accepted || !ReferenceEquals(opening.State, commands.Session.State)))
                throw new ArgumentException("Opening must belong to the attached battle.", nameof(opening));
            BeginChange();
            try
            {
                Install(commands, presentation, opening?.Before);
                if (opening != null) Playback.PresentCommitted(opening, animate);
            }
            finally { changingSession = false; }
        }

        private void Install(IBattleSessionCommands commands, IBattleSessionPresentation presentation, CardDuelState initialState = null)
        {
            // A different frontend must release its old animation while it still observes its old session.
            // Reusing a frontend resets its playback in Initialize, preserving existing scene rebind behavior.
            if (Playback != null && !ReferenceEquals(Playback.Presentation, presentation)) Playback.Synchronize();
            Playback = null;
            Commands = commands;
            // Initialize may synchronously read Host.Session and refresh UI. Never expose the old command port.
            // If it throws, retain the session for recovery, but keep commands unavailable until reattachment.
            Playback = new BattleSessionPlayback(commands, presentation, initialState);
            SessionAttached?.Invoke();
        }

        public void Synchronize()
        {
            if (Session == null) return;
            BeginChange();
            try
            {
                if (Playback == null) throw new InvalidOperationException("Reattach a presentation after initialization failed.");
                Playback.Synchronize();
            }
            finally { changingSession = false; }
        }

        private void BeginChange()
        {
            if (changingSession) throw new InvalidOperationException("A battle lifecycle operation is already in progress.");
            changingSession = true;
        }
    }
}
