// Hardens against DLL search-order hijacking: every P/Invoke in this assembly
// targets system DLLs in System32; restrict the OS search path accordingly.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.System32)]
