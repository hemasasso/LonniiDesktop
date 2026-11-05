using Lonnii.Setup;

// Internal tool: issues the encrypted credentials file for a new customer, the repair codes
// for a till that cannot find its server, and the offline password reset codes for a shop owner. See SetupCommand and RepairCommand for why
// these are a separate program rather than a verb on the API.
return args.FirstOrDefault() switch
{
    "repair-keygen" => RepairCommand.Keygen(args[1..]),
    // The licence key is its own pair (see LicenceToken): never the repair/update key.
    "licence-keygen" => RepairCommand.Keygen(args[1..], RepairCommand.LicenceKeyPath, "LicenceToken.PublicKey"),
    "repair-code" => RepairCommand.Issue(args[1..]),
    "reset-code" => RepairCommand.IssueReset(args[1..]),
    "release" => ReleaseCommand.Run(args[1..]),
    _ => SetupCommand.Run(args),
};
