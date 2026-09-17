namespace AbyssScav.Persistence;

/// <summary>Save error codes (docs/16 §11 SAVE-* domain).</summary>
public static class SaveErrorCodes
{
    public const string Write = "SAVE-001";
    public const string BackupRestore = "SAVE-002";
    public const string Corrupt = "SAVE-003";
    public const string FutureVersion = "SAVE-004";
    public const string Migration = "SAVE-005";
}

/// <summary>Base save failure: code + diagnostic detail, no fake success.</summary>
public class SaveException : Exception
{
    public string Code { get; }

    public SaveException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public SaveException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>Primary file is truncated / invalid JSON / missing schema_version.</summary>
public sealed class SaveCorruptException : SaveException
{
    public SaveCorruptException(string message)
        : base(SaveErrorCodes.Corrupt, message)
    {
    }

    public SaveCorruptException(string message, Exception inner)
        : base(SaveErrorCodes.Corrupt, message, inner)
    {
    }
}

/// <summary>Schema newer than supported: read-only, write refused.</summary>
public sealed class SaveFutureVersionException : SaveException
{
    public int FoundVersion { get; }

    public SaveFutureVersionException(int foundVersion)
        : base(SaveErrorCodes.FutureVersion,
            $"Save schema v{foundVersion} is newer than supported v{ProfileSave.CurrentSchemaVersion}. Refusing to write.")
    {
        FoundVersion = foundVersion;
    }
}

/// <summary>Migration chain broken or a step failed; original file preserved.</summary>
public sealed class SaveMigrationException : SaveException
{
    public SaveMigrationException(string message)
        : base(SaveErrorCodes.Migration, message)
    {
    }

    public SaveMigrationException(string message, Exception inner)
        : base(SaveErrorCodes.Migration, message, inner)
    {
    }
}

/// <summary>Atomic write / backup / restore IO failure; primary preserved.</summary>
public sealed class SaveIOException : SaveException
{
    public SaveIOException(string code, string message, Exception inner)
        : base(code, message, inner)
    {
    }
}
