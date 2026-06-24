using System.Runtime.InteropServices;
using System.Text;
using WindowsCareKit.Core.Modules.Migration.Detection;

namespace WindowsCareKit.Win32;

/// <summary>Read-only MSI product catalog backed by msi.dll. It never uses Win32_Product/WMI.</summary>
public sealed class Win32MsiCatalog : IMsiCatalog
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;
    private const uint MsiInstallContextUserManaged = 1;
    private const uint MsiInstallContextUserUnmanaged = 2;
    private const uint MsiInstallContextMachine = 4;
    private const uint MsiInstallContextAll = MsiInstallContextUserManaged | MsiInstallContextUserUnmanaged | MsiInstallContextMachine;

    public IReadOnlyList<MsiProduct> EnumerateProducts()
    {
        var products = new List<MsiProduct>();
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            var sid = new StringBuilder(256);
            uint sidChars = (uint)sid.Capacity;
            uint result = MsiEnumProductsEx(
                szProductCode: null,
                szUserSid: null,
                dwContext: MsiInstallContextAll,
                dwIndex: index,
                szInstalledProductCode: productCode,
                pdwInstalledContext: out uint context,
                szSid: sid,
                pcchSid: ref sidChars);

            if (result == ErrorNoMoreItems)
                break;
            if (result != ErrorSuccess)
                continue;

            string code = productCode.ToString();
            string? name = GetProductInfo(code, SidOrNull(sid), context, "ProductName");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            products.Add(new MsiProduct
            {
                ProductCode = code,
                DisplayName = name,
                Publisher = GetProductInfo(code, SidOrNull(sid), context, "Publisher"),
                Version = GetProductInfo(code, SidOrNull(sid), context, "VersionString"),
                InstallLocation = GetProductInfo(code, SidOrNull(sid), context, "InstallLocation"),
                UserSid = SidOrNull(sid),
                IsMachineContext = context == MsiInstallContextMachine,
            });
        }

        return products;
    }

    private static string? GetProductInfo(string productCode, string? sid, uint context, string property)
    {
        uint chars = 512;
        var value = new StringBuilder((int)chars);
        uint result = MsiGetProductInfoEx(productCode, sid, context, property, value, ref chars);
        if (result == ErrorMoreData)
        {
            value = new StringBuilder((int)chars + 1);
            result = MsiGetProductInfoEx(productCode, sid, context, property, value, ref chars);
        }

        if (result != ErrorSuccess)
            return null;

        string s = value.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string? SidOrNull(StringBuilder sid)
    {
        string s = sid.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsEx(
        string? szProductCode,
        string? szUserSid,
        uint dwContext,
        uint dwIndex,
        StringBuilder szInstalledProductCode,
        out uint pdwInstalledContext,
        StringBuilder szSid,
        ref uint pcchSid);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoEx(
        string szProductCode,
        string? szUserSid,
        uint dwContext,
        string szProperty,
        StringBuilder lpValue,
        ref uint pcchValue);
}
