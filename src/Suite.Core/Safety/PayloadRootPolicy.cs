namespace WindowsCareKit.Core.Safety;

/// <summary>Why a candidate payload root was refused. Defined only when the verdict is not allowed.</summary>
public enum PayloadRootRejection
{
    /// <summary>No path was supplied (null, empty, or whitespace). Deliberately value 0 so that a
    /// default-constructed verdict is coherent rather than "refused for no stated reason".</summary>
    NotProvided = 0,

    /// <summary>The path could not be turned into a full path at all.</summary>
    Unparseable,

    /// <summary>The path is not a local drive-letter path (UNC, device path, drive-relative).</summary>
    NotLocalDrivePath,

    /// <summary>The path equals, or lies inside, one of the forbidden roots.</summary>
    InsideForbiddenRoot,
}

/// <summary>
/// The outcome of <see cref="PayloadRootPolicy.Evaluate"/>. Only the policy can produce one: there is
/// no public constructor and no public factory, so no caller can fabricate an approval.
/// </summary>
public sealed class PayloadRootVerdict
{
    private PayloadRootVerdict(
        bool isAllowed,
        string normalizedRoot,
        PayloadRootRejection rejection,
        string matchedForbiddenRoot)
    {
        IsAllowed = isAllowed;
        NormalizedRoot = normalizedRoot;
        Rejection = rejection;
        MatchedForbiddenRoot = matchedForbiddenRoot;
    }

    /// <summary>True when the candidate may be used as a payload root.</summary>
    public bool IsAllowed { get; }

    /// <summary>The canonicalized candidate. Non-null; <see cref="string.Empty"/> unless allowed.</summary>
    public string NormalizedRoot { get; }

    /// <summary>Why it was refused. Meaningful only when <see cref="IsAllowed"/> is false.</summary>
    public PayloadRootRejection Rejection { get; }

    /// <summary>The forbidden root that matched. Non-null; empty unless
    /// <see cref="Rejection"/> is <see cref="PayloadRootRejection.InsideForbiddenRoot"/>.</summary>
    public string MatchedForbiddenRoot { get; }

    internal static PayloadRootVerdict Allowed(string normalizedRoot) =>
        new(true, normalizedRoot, PayloadRootRejection.NotProvided, string.Empty);

    internal static PayloadRootVerdict Rejected(PayloadRootRejection rejection) =>
        new(false, string.Empty, rejection, string.Empty);

    internal static PayloadRootVerdict RejectedInside(string matchedForbiddenRoot) =>
        new(false, string.Empty, PayloadRootRejection.InsideForbiddenRoot, matchedForbiddenRoot);
}

/// <summary>
/// The single owner of "may this directory be used as a payload root". Pure: it reads no ambient
/// process, environment or filesystem state — every forbidden root arrives as an explicit constructor
/// argument, and the set is a UNION so a future caller adds a root without editing this type.
/// </summary>
public sealed class PayloadRootPolicy
{
    /// <exception cref="ArgumentNullException"><paramref name="forbiddenRoots"/> is null.</exception>
    /// <exception cref="ArgumentException">The sequence is empty, or any element is null, blank, not
    /// fully qualified, or cannot be canonicalized.</exception>
    public PayloadRootPolicy(IEnumerable<string> forbiddenRoots)
    {
        ArgumentNullException.ThrowIfNull(forbiddenRoots);

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new ArgumentException("Every forbidden payload root must be a fully qualified path.", nameof(forbiddenRoots));

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("A forbidden payload root could not be canonicalized.", nameof(forbiddenRoots), ex);
            }

            if (seen.Add(full))
                normalized.Add(full);
        }

        if (normalized.Count == 0)
            throw new ArgumentException("At least one forbidden payload root is required.", nameof(forbiddenRoots));

        ForbiddenRoots = Array.AsReadOnly(normalized.ToArray());
    }

    /// <summary>The canonicalized forbidden roots, in the order supplied, duplicates removed
    /// (ordinal case-insensitive). Never empty.</summary>
    public IReadOnlyList<string> ForbiddenRoots { get; }

    /// <summary>Decide whether <paramref name="candidate"/> may be a payload root. Never throws.</summary>
    public PayloadRootVerdict Evaluate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return PayloadRootVerdict.Rejected(PayloadRootRejection.NotProvided);

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PayloadRootVerdict.Rejected(PayloadRootRejection.Unparseable);
        }

        if (full.Length < 2 || !char.IsLetter(full[0]) || full[1] != ':')
            return PayloadRootVerdict.Rejected(PayloadRootRejection.NotLocalDrivePath);

        foreach (string forbiddenRoot in ForbiddenRoots)
        {
            string prefix = Path.EndsInDirectorySeparator(forbiddenRoot)
                ? forbiddenRoot
                : forbiddenRoot + Path.DirectorySeparatorChar;
            if (string.Equals(full, forbiddenRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return PayloadRootVerdict.RejectedInside(forbiddenRoot);
        }

        return PayloadRootVerdict.Allowed(full);
    }
}
