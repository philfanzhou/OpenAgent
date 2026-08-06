namespace OpenAgent.Contracts.Authentication;

public sealed class PasswordLoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public sealed class MicrosoftTokenExchangeRequest
{
    public string Code { get; init; } = string.Empty;
    public string CodeVerifier { get; init; } = string.Empty;
    public string? RedirectUri { get; init; }
}
