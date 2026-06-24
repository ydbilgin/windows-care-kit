using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>One product returned by the MSI catalog (msi.dll), already read-only projected.</summary>
public sealed record MsiProduct
{
    public required string ProductCode { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public string? UserSid { get; init; }
    public bool IsMachineContext { get; init; }
}

/// <summary>Read-only abstraction over MSI product enumeration. Implementations must never use Win32_Product.</summary>
public interface IMsiCatalog
{
    IReadOnlyList<MsiProduct> EnumerateProducts();
}

/// <summary><see cref="IProgramSource"/> for the MSI product catalog.</summary>
public sealed class MsiProductSource : IProgramSource
{
    private readonly IMsiCatalog _catalog;
    private readonly IPathCanonicalizer _canon;
    private readonly string? _currentUserSid;

    public MsiProductSource(IMsiCatalog catalog, IPathCanonicalizer canon, string? currentUserSid = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _canon = canon ?? throw new ArgumentNullException(nameof(canon));
        _currentUserSid = string.IsNullOrWhiteSpace(currentUserSid) ? null : currentUserSid;
    }

    public ProgramSourceKind Kind => ProgramSourceKind.Msi;

    public ProgramEnumeration Enumerate()
    {
        IReadOnlyList<MsiProduct> products;
        try
        {
            products = _catalog.EnumerateProducts();
        }
        catch
        {
            return Fail();
        }

        if (products.Count == 0)
            return Fail();

        var programs = new List<DiscoveredProgram>(products.Count);
        foreach (MsiProduct product in products)
        {
            string? productCode = ProgramJoinKeys.TryProductCode(product.ProductCode);
            if (productCode is null || string.IsNullOrWhiteSpace(product.DisplayName))
                continue;

            string? leaf = ProgramJoinKeys.InstallPathLeaf(product.InstallLocation, _canon);
            string normalizedName = ProgramJoinKeys.NormalizeName(product.DisplayName);
            ProgramScope scope = ScopeOf(product);

            programs.Add(new DiscoveredProgram
            {
                Id = productCode,
                DisplayName = product.DisplayName.Trim(),
                Publisher = NullIfWhiteSpace(product.Publisher),
                Version = NullIfWhiteSpace(product.Version),
                InstallLocation = NullIfWhiteSpace(product.InstallLocation),
                InstallPathLeaf = leaf,
                ProductCode = productCode,
                NormalizedName = normalizedName,
                Scope = scope,
                Sources = [ProgramSourceKind.Msi],
                IsSystemComponent = false,
                ReinstallId = null,
                PackageFamilyName = null,
            });
        }

        if (programs.Count == 0)
            return Fail();

        return new ProgramEnumeration(programs, new ProgramSourceReport(ProgramSourceKind.Msi, ProgramSourceStatus.Ok, programs.Count));
    }

    private ProgramScope ScopeOf(MsiProduct product)
    {
        if (product.IsMachineContext)
            return ProgramScope.Machine;
        if (!string.IsNullOrWhiteSpace(product.UserSid)
            && !string.IsNullOrWhiteSpace(_currentUserSid)
            && !string.Equals(product.UserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase))
            return ProgramScope.OtherUserNotEnumerable;
        return ProgramScope.CurrentUser;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProgramEnumeration Fail() =>
        new([], new ProgramSourceReport(ProgramSourceKind.Msi, ProgramSourceStatus.SourceFailed, 0));
}
