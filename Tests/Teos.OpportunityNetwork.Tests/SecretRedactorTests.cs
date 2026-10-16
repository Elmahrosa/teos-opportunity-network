using Teos.OpportunityNetwork.Runner;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>
/// Pure unit tests for the log/error redaction used on the public-repo scheduled scanner.
/// No database, no network.
/// </summary>
public class SecretRedactorTests
{
    private const string ConnString =
        "Host=ep-cool-1.aws.neon.tech;Database=teos;Username=teos;Password=superSecretPw!;SSL Mode=Require";

    [Fact]
    public void Redacts_full_secret_value()
    {
        var result = SecretRedactor.RedactSecrets("boom: xyzToken-abc123 ", new[] { "xyzToken-abc123" });
        Assert.DoesNotContain("xyzToken-abc123", result);
        Assert.Contains(SecretRedactor.Replacement, result);
    }

    [Fact]
    public void Redacts_npgsql_connection_string_for_leaked_error_text()
    {
        var message = $"Exception while connecting: Timeout after 30s: {ConnString}";
        var result = SecretRedactor.RedactSecrets(message, new[] { ConnString });
        Assert.DoesNotContain("superSecretPw!", result);
        Assert.DoesNotContain("ep-cool-1", result);
        Assert.Contains(SecretRedactor.Replacement, result);
    }

    [Fact]
    public void Redacts_password_segment_even_when_full_string_is_absent()
    {
        // Npgsql sometimes echoes only the password segment in error text.
        var message = "Npgsql.NpgsqlException: Password=superSecretPw! authentication failed";
        var result = SecretRedactor.RedactSecrets(message, new[] { ConnString });
        Assert.DoesNotContain("superSecretPw!", result); // the credential value itself must never survive
        Assert.Contains("Password=*", result);           // structure stays readable, value is redacted
    }

    [Fact]
    public void Redacts_multiple_secrets_from_a_single_message()
    {
        var result = SecretRedactor.RedactSecrets(
            "failed with keyA=aaa and keyB=bbb", new[] { "aaa", "bbb" });
        Assert.DoesNotContain("aaa", result);
        Assert.DoesNotContain("bbb", result);
    }

    [Fact]
    public void Ignores_empty_and_null_seeds()
    {
        var result = SecretRedactor.RedactSecrets("keep this text", new string?[] { null, "", "   " });
        Assert.Equal("keep this text", result);
    }

    [Fact]
    public void Leaves_clean_text_untouched()
    {
        const string clean = "[runtime] semantic evaluator: neutral-50";
        Assert.Equal(clean, SecretRedactor.RedactSecrets(clean, new[] { ConnString }));
    }

    [Fact]
    public void Null_message_returns_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.RedactSecrets(null, new[] { "x" }));
    }
}