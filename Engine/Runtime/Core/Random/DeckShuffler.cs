using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace PokerEngine
{
    public sealed class ShuffleResult
    {
        public IReadOnlyList<CardInstanceId> Cards { get; }
        public RandomState Random { get; }
        public ShuffleResult(IEnumerable<CardInstanceId> cards, RandomState random)
        { Cards = Guard.View(Guard.Copy(cards, nameof(cards))); Random = random ?? throw new ArgumentNullException(nameof(random)); }
    }

    /// <summary>Pure contract: inputs are never mutated; failed commands must not consume external randomness.</summary>
    public interface IDeckShuffler { ShuffleResult Shuffle(IReadOnlyList<CardInstanceId> cards, RandomState random); }

    /// <summary>HMAC-SHA256 counter stream with rejection sampling and Fisher-Yates. Secret key stays on the authority.</summary>
    public sealed class HmacDeckShuffler : IDeckShuffler
    {
        public const string Algorithm = "hmac-sha256-counter-le";
        public static RandomState CreateRandomState()
        {
            var seed = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
            return FromSeed(seed);
        }
        public static RandomState FromSeed(IEnumerable<byte> seed)
        {
            var key = Guard.Copy(seed, nameof(seed));
            if (key.Length != 32) throw new ArgumentException("Expected a 256-bit secret seed.");
            return new RandomState(Algorithm, 1, key.Concat(new byte[8]));
        }
        public ShuffleResult Shuffle(IReadOnlyList<CardInstanceId> cards, RandomState random)
        {
            if (random == null || random.Algorithm != Algorithm || random.AlgorithmVersion != 1 || random.StateBytes.Count != 40)
                throw new ArgumentException("Unsupported shuffle state.");
            var deck = Guard.Copy(cards, nameof(cards)); var key = random.StateBytes.Take(32).ToArray();
            ulong counter = 0;
            for (int i = 0; i < 8; i++) counter |= (ulong)random.StateBytes[32 + i] << (8 * i);
            using (var hmac = new HMACSHA256(key))
            {
                for (int i = deck.Length - 1; i > 0; i--)
                {
                    uint bound = (uint)(i + 1), threshold = unchecked(0u - bound) % bound, value;
                    do
                    {
                        var input = CounterBytes(counter); counter = checked(counter + 1);
                        var hash = hmac.ComputeHash(input);
                        value = (uint)hash[0] | ((uint)hash[1] << 8) | ((uint)hash[2] << 16) | ((uint)hash[3] << 24);
                    } while (value < threshold);
                    int j = (int)(value % bound); var card = deck[i]; deck[i] = deck[j]; deck[j] = card;
                }
            }
            return new ShuffleResult(deck, new RandomState(Algorithm, 1, key.Concat(CounterBytes(counter))));
        }
        private static byte[] CounterBytes(ulong counter)
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++) bytes[i] = (byte)(counter >> (8 * i));
            return bytes;
        }
    }
}
