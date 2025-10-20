using System;

namespace FModel.Framework
{
    public class InvalidTokenException : Exception
    {
        public InvalidTokenException() : base("OAuth token invalid or expired") { }
    }
}
