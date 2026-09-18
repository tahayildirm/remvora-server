namespace Remvora.Infrastructure;
/// <summary>Validated deployment limits. Upper bounds constrain stolen-token and resource exposure.</summary>
public sealed class SecurityPolicy
{
    public int EnrollmentMinutes { get; set; } = 10;
    public int ChallengeSeconds { get; set; } = 60;
    public int SessionTicketSeconds { get; set; } = 60;
    public int UserSessionMinutes { get; set; } = 720;
    public int RemoteSessionMinutes { get; set; } = 60;
    public bool IsValid() => EnrollmentMinutes is >= 1 and <= 30 && ChallengeSeconds is >= 15 and <= 120 && SessionTicketSeconds is >= 15 and <= 120 && UserSessionMinutes is >= 60 and <= 1440 && RemoteSessionMinutes is >= 1 and <= 240;
}
