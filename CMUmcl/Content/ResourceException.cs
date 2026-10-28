using System;

namespace CMUmcl.Content
{
    /// <summary>Raised when the resource site protocol fails (signature, allowlist, network, hash).</summary>
    public sealed class ResourceException : Exception
    {
        public ResourceException(string message) : base(message) { }
        public ResourceException(string message, Exception inner) : base(message, inner) { }
    }
}
