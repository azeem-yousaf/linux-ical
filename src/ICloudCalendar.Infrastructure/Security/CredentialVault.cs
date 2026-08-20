using System.Diagnostics;

namespace ICloudCalendar.Infrastructure.Security;

public interface ICredentialVault
{
    Task StoreAsync(string accountId, string secret, CancellationToken cancellationToken = default);
    Task<string?> RetrieveAsync(string accountId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed record SecretToolResult(int ExitCode, string StandardOutput, string StandardError);

public interface ISecretToolRunner
{
    Task<SecretToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken);
}

public sealed class SecretToolCredentialVault(ISecretToolRunner runner) : ICredentialVault
{
    private const string ServiceName = "linux-icloud-calendar";

    public async Task StoreAsync(
        string accountId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var result = await runner.RunAsync(
            ["store", "--label=Linux iCloud Calendar", "service", ServiceName, "account", accountId],
            secret,
            cancellationToken);
        EnsureSuccess(result, "store");
    }

    public async Task<string?> RetrieveAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        var result = await runner.RunAsync(
            ["lookup", "service", ServiceName, "account", accountId],
            null,
            cancellationToken);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        EnsureSuccess(result, "retrieve");
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    public async Task DeleteAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        var result = await runner.RunAsync(
            ["clear", "service", ServiceName, "account", accountId],
            null,
            cancellationToken);
        if (result.ExitCode is not 0 and not 1)
        {
            EnsureSuccess(result, "delete");
        }
    }

    private static void ValidateAccountId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (accountId.Length > 128 || accountId.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("The account identifier is invalid.", nameof(accountId));
        }
    }

    private static void EnsureSuccess(SecretToolResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The Linux Secret Service is unavailable."
                : result.StandardError.Trim();
            throw new InvalidOperationException($"Could not {operation} the calendar credential. {reason}");
        }
    }
}

public sealed class SecretToolProcessRunner : ISecretToolRunner
{
    public async Task<SecretToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "secret-tool",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start secret-tool.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "secret-tool is not installed. Install libsecret (or libsecret-tools) to store iCloud credentials securely.",
                exception);
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), cancellationToken);
        }
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new SecretToolResult(process.ExitCode, await outputTask, await errorTask);
    }
}
