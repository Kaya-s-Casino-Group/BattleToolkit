using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public sealed class StateValidationException : InvalidOperationException
    {
        public string Code { get; }
        public string Path { get; }
        public StateValidationException(string code, string path) : base(code + " at " + path) { Code = code; Path = path; }
    }

}
