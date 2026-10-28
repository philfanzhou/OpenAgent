namespace OpenAgent.Contracts.Authentication;

public sealed class PasswordLoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
