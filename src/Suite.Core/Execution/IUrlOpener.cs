namespace WindowsCareKit.Core.Execution;

/// <summary>Opens trusted external links from user-initiated UI actions.</summary>
public interface IUrlOpener
{
    void Open(Uri uri);
}
