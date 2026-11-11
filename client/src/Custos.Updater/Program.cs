using Custos.Core;

// Placeholder: the update procedure (appendix A.10) is implemented together with the monitoring server.
// For now the program only reports its version, so that the installer and build can already ship it.
Console.WriteLine($"Custos.Updater {CustosInfo.Version}");

if (args.Length == 0 || args[0] is "--version" or "-v")
{
    return 0;
}

Console.Error.WriteLine("Updating is not implemented yet.");
return 2;
