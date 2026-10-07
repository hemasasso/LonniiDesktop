using Lonnii.Setup;

// Internal tool: issues the encrypted credentials file for a new customer, and the repair
// codes for a till that cannot find its server. See SetupCommand and RepairCommand for why
// these are a separate program rather than a verb on the API.
return args.FirstOrDefault() switch
{
    "repair-keygen" => RepairCommand.Keygen(args[1..]),
    "repair-code" => RepairCommand.Issue(args[1..]),
    _ => SetupCommand.Run(args),
};
