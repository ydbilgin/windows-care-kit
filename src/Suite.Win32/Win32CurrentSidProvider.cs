using System.Security.Principal;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Win32;

/// <summary>Resolves the current Windows user's SID for HKU registry scoping.</summary>
public sealed class Win32CurrentSidProvider : ICurrentSidProvider
{
    public string? GetCurrentSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }
}
