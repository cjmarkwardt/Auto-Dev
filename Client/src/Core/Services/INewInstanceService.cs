namespace AutoDev.Core.Services;

/// <summary>Seam for launching a brand-new, fully independent AutoDev process - one instance now only ever opens a single workspace, so opening another workspace at the same time means starting another instance.</summary>
public interface INewInstanceService
{
    /// <summary>Starts a new AutoDev process with no arguments, so it opens empty rather than restoring anything - best-effort, never throws.</summary>
    void OpenNewInstance();
}
