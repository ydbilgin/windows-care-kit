namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// One post-copy integrity row for a single LEAF file inside the payload tree (spec §1.3 verify intent).
/// It is computed from the DESTINATION after the copy ran, so it proves what actually landed on the backup
/// media — and it deliberately records ONLY destination-relative facts: there is no source path here, so
/// nothing about the original on-disk layout (username/profile) travels off-machine and no redaction is
/// needed (locked decision #4).
/// </summary>
/// <param name="EntryId">The manifest/copy-action id whose copy produced this leaf (groups leaves by entry).</param>
/// <param name="DestinationRelativePath">The leaf path RELATIVE to the payload root (forward/native separators as enumerated).</param>
/// <param name="Sha256">The lowercase-hex SHA-256 of the copied leaf's bytes, read from the destination.</param>
/// <param name="ByteSize">The copied leaf's size in bytes.</param>
/// <param name="CopiedUtc">When the integrity row was produced (the backup run instant, from the clock).</param>
public sealed record BackupIntegrity(
    string EntryId,
    string DestinationRelativePath,
    string Sha256,
    long ByteSize,
    DateTime CopiedUtc);
