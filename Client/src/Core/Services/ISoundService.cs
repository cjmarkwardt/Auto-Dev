namespace AutoDev.Core.Services;

public interface ISoundService
{
    /// <summary>Plays a short, best-effort audible ding - never throws, never blocks the caller.</summary>
    void PlayDing();
}
