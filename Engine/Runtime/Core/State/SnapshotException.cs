using System;
namespace PokerEngine
{
    public sealed class SnapshotException : InvalidOperationException
    {
        public SnapshotException(string message, Exception inner = null) : base(message, inner) { }
    }

}
