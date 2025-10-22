using System;

public class InvalidTokenException : Exception
{
    public InvalidTokenException() : base("OAuth token invalid or expired") { }
}
