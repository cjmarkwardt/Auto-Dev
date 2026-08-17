using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoDev.Core.Services;

/// <summary>
/// No bundled audio asset: on Linux the ding is a synthesized sine-wave tone piped as raw PCM to
/// whatever player is on PATH, so it doesn't depend on a particular desktop's sound theme having any
/// specific themed sound installed - only on aplay/paplay existing, which is true of virtually every
/// Linux desktop (ALSA/PulseAudio come with one or the other). Windows has no such variance, so it just
/// uses the OS's own notification beep via MessageBeep.
///
/// On Linux, PlayDing spawns a real, independent OS child process (aplay/paplay) to actually play the
/// tone - disposing the .NET Process wrapper (the previous approach here) only releases .NET-side
/// handles, it does NOT stop that child, which keeps playing on its own even after AutoDev itself has
/// exited (a `using var process = Process.Start(...)` reads as if it kills the process on scope exit, but
/// it doesn't). App.axaml.cs disposes this service (as part of disposing the whole DI ServiceProvider) on
/// both a normal window close and a caught termination signal - Dispose here kills every still-running
/// player process it ever spawned, so a ding genuinely cannot outlive the app for any shutdown path this
/// process gets a chance to react to (a hard SIGKILL is a fundamental exception no process, AutoDev or
/// otherwise, can react to at all - only cooperative shutdowns are something code can guarantee against).
/// </summary>
public sealed class SoundService : ISoundService, IDisposable
{
    private const int SampleRateHz = 44100;
    private const double DurationSeconds = 0.18;
    private const double FrequencyHz = 880.0;

    /// <summary>Fraction of full scale (short.MaxValue) the sine wave peaks at - kept short of 1.0 so the linear fade-out envelope has no headroom to clip on the way down.</summary>
    private const double GainFraction = 0.9;

    private static readonly Lazy<byte[]> DingPcm = new(GenerateDingPcm);

    private readonly object _lock = new();
    private readonly HashSet<Process> _activePlayers = [];
    private bool _disposed;

    public void PlayDing()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                PlayWindowsBeep();
            }
            else if (OperatingSystem.IsLinux())
            {
                PlayLinuxDing();
            }
        }
        catch
        {
            // Best-effort notification sound - a missing player binary or audio backend should never
            // crash or interrupt the app.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    private const uint MB_ICONASTERISK = 0x00000040;

    private static void PlayWindowsBeep() => MessageBeep(MB_ICONASTERISK);

    private void PlayLinuxDing()
    {
        if (TryPlayRawPcm("aplay", ["-q", "-t", "raw", "-r", SampleRateHz.ToString(), "-c", "1", "-f", "S16_LE", "-"]))
        {
            return;
        }

        TryPlayRawPcm("paplay", ["--raw", $"--rate={SampleRateHz}", "--channels=1", "--format=s16le"]);
    }

    private bool TryPlayRawPcm(string fileName, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Track it so Dispose (see App.axaml.cs) can kill it outright if the app shuts down before it
            // finishes on its own - registered before writing any audio so there's no window where a
            // just-spawned player exists but isn't yet tracked. EnableRaisingEvents + Exited untracks and
            // disposes it the moment it finishes normally, so _activePlayers only ever holds genuinely
            // still-running players, never a backlog of stale handles.
            lock (_lock)
            {
                if (_disposed)
                {
                    KillAndDispose(process);
                    return false;
                }

                _activePlayers.Add(process);
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (_lock)
                {
                    _activePlayers.Remove(process);
                }

                process.Dispose();
            };

            process.StandardInput.BaseStream.Write(DingPcm.Value);
            process.StandardInput.BaseStream.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Called once, when the app itself is shutting down (see App.axaml.cs) - kills every player process still running rather than letting it finish on its own after AutoDev is already gone.</summary>
    public void Dispose()
    {
        List<Process> toKill;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toKill = [.. _activePlayers];
            _activePlayers.Clear();
        }

        foreach (var process in toKill)
        {
            KillAndDispose(process);
        }
    }

    private static void KillAndDispose(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Already exited between the check and the call, or never had permission to signal it -
            // either way, there's nothing left for us to clean up.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static byte[] GenerateDingPcm()
    {
        var sampleCount = (int)(SampleRateHz * DurationSeconds);
        var buffer = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)SampleRateHz;
            var envelope = 1.0 - t / DurationSeconds; // linear fade-out avoids an audible click at the end
            var sample = Math.Sin(2 * Math.PI * FrequencyHz * t) * envelope * short.MaxValue * GainFraction;
            var value = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(value).CopyTo(buffer, i * 2);
        }

        return buffer;
    }
}
