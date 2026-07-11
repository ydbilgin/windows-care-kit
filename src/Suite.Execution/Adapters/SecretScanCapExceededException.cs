namespace WindowsCareKit.Execution.Adapters;

internal sealed class SecretScanCapExceededException : System.IO.IOException
{
    public SecretScanCapExceededException(string message) : base(message) { }
}
