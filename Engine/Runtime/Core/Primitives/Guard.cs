using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public static class Guard
    {
        public static string Text(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A nonempty identifier is required.", name);
            return value;
        }

        public static T[] Copy<T>(IEnumerable<T> values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            var copy = values.ToArray();
            if (copy.Any(value => ReferenceEquals(value, null))) throw new ArgumentException("Null elements are not allowed.", name);
            return copy;
        }

        public static IReadOnlyList<T> View<T>(T[] values) => Array.AsReadOnly(values);
    }
}
