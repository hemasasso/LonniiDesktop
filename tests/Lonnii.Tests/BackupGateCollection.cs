namespace Lonnii.Tests;

/// <summary>
/// Tests that run a cloud backup pass. CloudBackupRunner allows one pass at a time per process -
/// right on a shop's computer, where one process is one shop's host - but the tests share a
/// process: run side by side, one class's pass would make the other's step aside and do nothing.
/// </summary>
[CollectionDefinition(Name)]
public sealed class BackupGateCollection
{
    public const string Name = "Cloud backup gate";
}
