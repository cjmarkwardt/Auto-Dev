using System.Text.RegularExpressions;
using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// The branch-identity convention this app layers on top of plain git: a branch's id is its literal git
/// branch name (no "version/"/"feature/" prefixing), and right after creating one, an *empty* commit is made
/// with a message of the form "{name} ~{star}[{parentId}>{id}]" (star is "*" iff the branch is private, else
/// ""; parentId is empty for a root branch, e.g. "Main ~[>main]") - this "base commit" is the branch's single
/// source of truth for its own name/parent/public-vs-private status.
///
/// Public vs. private is a collaboration model, not a naming convention: a public branch (e.g. "main", or any
/// other long-lived branch meant for many users to work on at once) is never squashed or renamed away (see
/// VersionActionState's CanSquash/CanRename), so its full history stays intact for everyone sharing it. A
/// private branch is meant for exactly one user at a time and is expected to be squashed and merged/deleted
/// once done - its history is disposable, not something other users depend on.
/// </summary>
public static class BranchConvention
{
    private static readonly Regex BaseCommitPattern = new(@"^(?<name>.+) ~(?<star>\*?)\[(?<parentId>[^>\]]*)>(?<id>[^\]]+)\]$", RegexOptions.Compiled);

    /// <summary>POSIX extended-regex grep pattern (for IGitService.FindFirstCommitMatchingAsync) matching any base-commit-shaped subject - deliberately looser than BaseCommitPattern (git's regex engine differs from .NET's); the exact structure is re-validated by TryParseBaseCommitMessage in C# afterward.</summary>
    public const string BaseCommitGrepPattern = @"~\*?\[[^]]*>[^]]+\]$";

    /// <summary>Lowercase, non-alphanumeric runs collapsed to a single "-", leading/trailing "-" trimmed - the auto-derived branch id shown (and editable) in the Create Branch dialog.</summary>
    public static string Slugify(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static string BuildBaseCommitMessage(string name, string? parentId, bool isPublic, string id) =>
        $"{name} ~{(isPublic ? "" : "*")}[{parentId}>{id}]";

    public static bool TryParseBaseCommitMessage(string message, out string name, out string? parentId, out bool isPublic, out string id)
    {
        var match = BaseCommitPattern.Match(message);
        if (!match.Success)
        {
            name = "";
            parentId = null;
            isPublic = false;
            id = "";
            return false;
        }

        name = match.Groups["name"].Value;
        var parsedParentId = match.Groups["parentId"].Value;
        parentId = parsedParentId.Length > 0 ? parsedParentId : null;
        isPublic = match.Groups["star"].Value != "*";
        id = match.Groups["id"].Value;
        return true;
    }

    /// <summary>
    /// A branch's base commit "can always be found by going through commits looking for one that ends with
    /// '>{id}]'" - this is that lookup: walks `id`'s own history (a branch's id is its literal git branch
    /// name, so `id` is directly usable as a ref) for the newest commit whose message ends with `>{id}]`,
    /// regardless of whether it's public or private (that star sits right after "~", not near `id` - see
    /// BuildBaseCommitMessage). Searching by the EXACT id (rather than "the nearest marker of any shape") is
    /// essential once a branch has been merged into: after a fast-forward Merge, the parent's tip commit is
    /// literally the just-merged child's own base-commit marker, so a generic "nearest marker" search from
    /// the parent's tip would wrongly resolve to the child's identity instead of the parent's - searching for
    /// this specific id's marker skips right past that and finds the parent's own, further back. Null if no
    /// such marker exists (a repo/branch that predates this convention, or the id doesn't exist).
    /// </summary>
    public static async Task<BranchInfo?> FindBranchInfoByIdAsync(IGitService git, string workspacePath, string id, CancellationToken cancellationToken = default)
    {
        var pattern = $@"~\*?\[[^]]*>{Regex.Escape(id)}\]$";
        var commit = await git.FindFirstCommitMatchingAsync(workspacePath, id, pattern, cancellationToken);
        if (commit is null || !TryParseBaseCommitMessage(commit.Subject, out var name, out var parentId, out var isPublic, out var parsedId))
        {
            return null;
        }

        return new BranchInfo(parsedId, name, parentId, isPublic, commit.Hash);
    }

    /// <summary>
    /// For a detached ref (a tag or an arbitrary commit, where there's no known branch id to search for
    /// directly - see FindBranchInfoByIdAsync): walks `refName`'s history for the newest commit shaped like
    /// ANY base-commit marker, and parses it into the "conceptual" branch that position belongs to. Used only
    /// where the id genuinely isn't known upfront (GetCurrentTargetAsync's detached-HEAD path,
    /// CreateBranchAsync's parent-id lookup when branching from a detached position) - everywhere else the
    /// branch id is already in hand and FindBranchInfoByIdAsync's precise search should be used instead.
    /// </summary>
    public static async Task<BranchInfo?> FindContainingBranchInfoAsync(IGitService git, string workspacePath, string refName, CancellationToken cancellationToken = default)
    {
        var commit = await git.FindFirstCommitMatchingAsync(workspacePath, refName, BaseCommitGrepPattern, cancellationToken);
        if (commit is null || !TryParseBaseCommitMessage(commit.Subject, out var name, out var parentId, out var isPublic, out var id))
        {
            return null;
        }

        return new BranchInfo(id, name, parentId, isPublic, commit.Hash);
    }
}
