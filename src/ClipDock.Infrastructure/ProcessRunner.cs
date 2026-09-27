using System.Diagnostics;
using System.Text;

namespace ClipDock.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? outputLine = null,
        Action<string>? errorLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => Append(eventArgs.Data, output, outputLine);
        process.ErrorDataReceived += (_, eventArgs) => Append(eventArgs.Data, error, errorLine);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException) { }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static void Append(string? line, StringBuilder builder, Action<string>? callback)
    {
        if (line is null) return;
        if (builder.Length < 2_000_000) builder.AppendLine(line);
        callback?.Invoke(line);
    }
}
