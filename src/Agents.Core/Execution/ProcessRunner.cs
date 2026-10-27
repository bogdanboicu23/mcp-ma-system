using System.Diagnostics;
using System.Text;

namespace Agents.Core.Execution;

/// <summary>
/// Starts a process and captures its output exactly (no line-ending changes, no BOM), because file
/// edits are done by reading a file through stdout and writing it back through stdin.
/// </summary>
public static class ProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public static async Task<ExecResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        string? stdin,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // always, so a command waiting for input sees EOF instead of hanging
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            StandardInputEncoding = utf8,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The process exited before reading everything; its exit code tells the story.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ExecResult(-1, await Drain(stdout).ConfigureAwait(false),
                await Drain(stderr).ConfigureAwait(false), TimedOut: true);
        }

        return new ExecResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            TimedOut: false);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    // A grandchild can keep the pipe open after the kill; do not wait for it forever.
    private static async Task<string> Drain(Task<string> reader)
    {
        var finished = await Task.WhenAny(reader, Task.Delay(DrainGrace)).ConfigureAwait(false);
        return finished == reader ? await reader.ConfigureAwait(false) : string.Empty;
    }
}
