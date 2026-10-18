using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Immutable personal hand levels. Missing categories default to level one for existing setups.</summary>
    public sealed class BattleHandLevels
    {
        public IReadOnlyDictionary<HandCategory, int> Levels { get; }

        public BattleHandLevels(IEnumerable<KeyValuePair<HandCategory, int>> levels = null)
        {
            var snapshot = Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().ToDictionary(category => category, category => 1);
            var seen = new HashSet<HandCategory>();
            foreach (var entry in levels ?? Enumerable.Empty<KeyValuePair<HandCategory, int>>())
            {
                if (!snapshot.ContainsKey(entry.Key) || entry.Value < 1 || !seen.Add(entry.Key))
                    throw new ArgumentException("Hand levels require distinct known categories and positive levels.", nameof(levels));
                snapshot[entry.Key] = entry.Value;
            }
            Levels = new ReadOnlyDictionary<HandCategory, int>(snapshot);
        }

        public int LevelFor(HandCategory category) => Levels.TryGetValue(category, out var level)
            ? level : throw new ArgumentOutOfRangeException(nameof(category));
    }
}
