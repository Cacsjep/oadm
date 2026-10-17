using Oadm.LicenseTool;

// Usage: Oadm.LicenseTool keys [--out <folder>]
//        Oadm.LicenseTool sign --key <private-key.pem> --plugin <plugin id> --server <server ID> --customer <name>
//                              [--expires yyyy-MM-dd] [--features a,b] [--limit name=value]... [--out <file>]
return LicenseCommands.Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory(), DateOnly.FromDateTime(DateTime.Today));
