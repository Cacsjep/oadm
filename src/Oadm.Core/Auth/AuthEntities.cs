using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>An OADM user (table Users). The password is stored only as a PBKDF2 hash (<see cref="PasswordHasher"/>).</summary>
public sealed class UserEntity
{
    public Guid Id { get; set; }

    /// <summary>As entered (1..64 characters).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Upper-case invariant user name: unique, so names are unique case-insensitively.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public bool Disabled { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    public override string ToString() => $"User {UserName} ({Role})";
}

/// <summary>A login session (table AuthTokens): only the SHA-256 of the token is stored.</summary>
public sealed class AuthTokenEntity
{
    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public string ClientAddress { get; set; } = string.Empty;

    public bool Remember { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Sliding: moved forward on use (written at most once per minute).</summary>
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>One audit log entry (table AuditEntries).</summary>
public sealed class AuditEntryEntity
{
    public long Id { get; set; }

    public DateTime TimeUtc { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string ClientAddress { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}
