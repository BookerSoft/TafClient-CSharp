using System.Security.Cryptography;
using System.Text;

namespace TafClient.Service;

/// <summary>
/// Generates a Windows .reg file that, when double-clicked and approved via
/// the OS's own UAC prompt, writes exactly the same DirectPlay registry
/// entries that talauncher.exe --registerdplay would write natively via
/// dplayreg::RegisterDplayLobbyableApplication.
///
/// Why this exists: --registerdplay's real elevation path (RunAs/UAC) does
/// not work reliably under Wine, since RunAs is a genuine Windows-only
/// mechanism Wine does not meaningfully implement, and even on real Windows,
/// requiring the whole app to run elevated just for this one step is
/// heavyweight. A standalone .reg file lets the user grant elevation for
/// exactly this one registry write, once, via the normal Windows UAC consent
/// dialog — no admin relaunch of the whole client needed.
///
/// Registry layout confirmed from the real C++ source
/// (libs/dplayreg/DPlayReg.cpp, RegisterDplayLobbyableApplication):
///   HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\DirectPlay\Applications\{AppName}
/// (WOW6432Node because talauncher.exe is a 32-bit binary, redirected there
/// by WOW64 on 64-bit Windows — see the comment at the actual key-path
/// construction below for the full explanation, confirmed against
/// talauncher.exe's own PE header.)
///     Guid             (REG_SZ) — UUIDv5(namespace, MOD_TECHNICAL_UPPERCASE)
///     Path             (REG_SZ) — the game's install directory
///     File             (REG_SZ) — the game exe filename (e.g. TotalA.exe)
///     CommandLine      (REG_SZ) — "-c TAForever.ini" (plus any extra game args)
///     CurrentDirectory (REG_SZ) — same as Path
/// </summary>
public static class DPlayRegFileGenerator
{
    // From talauncher.cpp: DEFAULT_DPLAY_REGISTERED_GAME_GUID, used as the
    // namespace for the UUIDv5 derivation. This is NOT itself the app's
    // GUID — it's the fixed seed namespace every mod's GUID is derived from.
    private static readonly Guid Namespace = Guid.Parse("1336f32e-d116-4633-b853-4fee1ec91ea5");

    /// <summary>
    /// Computes the same GUID talauncher would derive for this mod, via a
    /// manual RFC 4122 UUIDv5 implementation (namespace + SHA-1), matching
    /// Qt's QUuid::createUuidV5 exactly — confirmed by Qt's own docs to be
    /// "as described by RFC 4122" with no Qt-specific deviation. Verified
    /// against Python's uuid.uuid5() (also RFC 4122 compliant) for several
    /// known mod names before relying on this in the actual generator.
    /// </summary>
    public static Guid ComputeDplayGuid(string modTechnicalUppercase)
    {
        // RFC 4122 §4.3: hash = SHA1(namespace_bytes_be + name_bytes)
        Span<byte> nsBytes = stackalloc byte[16];
        Namespace.TryWriteBytes(nsBytes); // .NET Guid bytes are little-endian internally for the first 3 fields
        // RFC 4122 requires the namespace in NETWORK (big-endian) byte order
        // for the first three fields (time_low, time_mid, time_hi_and_version).
        // Guid.TryWriteBytes writes those three fields little-endian, so we
        // must byte-swap them before hashing — this is the single most
        // common source of a "looks right but doesn't match" UUIDv5 bug.
        Span<byte> nsBytesBE = stackalloc byte[16];
        nsBytesBE[0] = nsBytes[3]; nsBytesBE[1] = nsBytes[2]; nsBytesBE[2] = nsBytes[1]; nsBytesBE[3] = nsBytes[0];
        nsBytesBE[4] = nsBytes[5]; nsBytesBE[5] = nsBytes[4];
        nsBytesBE[6] = nsBytes[7]; nsBytesBE[7] = nsBytes[6];
        for (int i = 8; i < 16; i++) nsBytesBE[i] = nsBytes[i]; // last 8 bytes are already in correct order

        byte[] nameBytes = Encoding.UTF8.GetBytes(modTechnicalUppercase);
        byte[] toHash = new byte[16 + nameBytes.Length];
        nsBytesBE.CopyTo(toHash);
        nameBytes.CopyTo(toHash, 16);

        byte[] hash = SHA1.HashData(toHash); // 20 bytes; we use the first 16

        Span<byte> result = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(result);

        // Set version (5) in the high nibble of byte 6, and variant (10xx) in
        // the high bits of byte 8 — standard RFC 4122 version/variant stamping.
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        // Convert back from network (big-endian) byte order to .NET's
        // internal little-endian representation for the first three fields,
        // same swap as above but in reverse, so Guid's string formatting
        // produces the standard hex representation.
        Span<byte> resultLE = stackalloc byte[16];
        resultLE[0] = result[3]; resultLE[1] = result[2]; resultLE[2] = result[1]; resultLE[3] = result[0];
        resultLE[4] = result[5]; resultLE[5] = result[4];
        resultLE[6] = result[7]; resultLE[7] = result[6];
        for (int i = 8; i < 16; i++) resultLE[i] = result[i];

        return new Guid(resultLE);
    }

    /// <summary>
    /// Builds the .reg file content for one mod. gamePath should be the
    /// install directory (no trailing slash needed — backslashes are escaped
    /// <summary>
    /// Checks whether this mod's DirectPlay application registration already
    /// exists in the registry — i.e. whether a previously-imported .reg file
    /// (or a successful --registerdplay run) has already taken effect. Reads
    /// directly via Microsoft.Win32.Registry rather than shelling out, since
    /// this is a simple read with no elevation required (HKLM reads don't
    /// need admin rights, only writes do).
    ///
    /// Windows-only: returns false unconditionally on any other OS, since
    /// the whole DirectPlay registration concept only applies there.
    /// </summary>
    public static bool IsRegistered(string modTechnical)
    {
        if (!OperatingSystem.IsWindows()) return false;

        string modUpper = modTechnical.ToUpperInvariant();
        string appName = $"Total Annihilation Forever ({modUpper})";
        // Same WOW6432Node reasoning as BuildRegFileContent below — talauncher
        // is a 32-bit process, so its own reads (and ours, to check the same
        // thing it would see) need to target the WOW64-redirected location
        // explicitly rather than relying on .NET's own (64-bit-by-default)
        // process redirection to happen to land in the same place.
        string subKeyPath = $@"SOFTWARE\WOW6432Node\Microsoft\DirectPlay\Applications\{appName}";

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (key is null) return false;
            // Presence of the key with a non-empty Path/File is what
            // dplayreg::CheckDplayLobbyableApplication itself checks for —
            // an empty/partial key (e.g. from an interrupted write) should
            // still be treated as "not registered" so the user gets
            // prompted to fix it rather than silently failing later.
            var path = key.GetValue("Path") as string;
            var file = key.GetValue("File") as string;
            return !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DPlayReg] IsRegistered check failed for {modTechnical}: {ex.Message}");
            return false; // fail safe — assume not registered, let the user re-confirm
        }
    }

    /// for .reg format regardless). gameExe is just the filename (e.g.
    /// "TotalA.exe"). extraGameArgs, if any, are prepended to the required
    /// "-c TAForever.ini" — matching talauncher's own ADDITIONAL_GAME_ARGS
    /// behavior exactly (see GpgNetApp/Program.cs's LaunchGameAsync for the
    /// same requirement on the direct-launch side).
    /// </summary>
    public static string BuildRegFileContent(string modTechnical, string gamePath, string gameExe, string? extraGameArgs = null)
    {
        string modUpper = modTechnical.ToUpperInvariant();
        Guid guid = ComputeDplayGuid(modUpper);
        string appName = $"Total Annihilation Forever ({modUpper})";
        string commandLine = string.IsNullOrWhiteSpace(extraGameArgs)
            ? "-c TAForever.ini"
            : $"{extraGameArgs.Trim()} -c TAForever.ini";

        // talauncher.exe is a 32-bit binary (confirmed directly from its PE
        // header: Machine type 0x014c = IMAGE_FILE_MACHINE_I386). On 64-bit
        // Windows, a 32-bit process's accesses to HKLM\SOFTWARE\... are
        // transparently redirected by WOW64 to
        // HKLM\SOFTWARE\WOW6432Node\... — but only for the PROCESS doing the
        // reading/writing. regedit.exe importing a .reg file runs as a
        // native 64-bit process by default on 64-bit Windows, so a .reg file
        // that targets the plain "SOFTWARE\..." path writes to the NATIVE
        // 64-bit view, which talauncher (32-bit, redirected) never looks at
        // at all — two genuinely separate registry locations. This was
        // confirmed as the actual cause of "imported successfully but the
        // game still says no DirectPlay registry entry exists": the import
        // had no error because it wrote somewhere real, just not the place
        // a 32-bit talauncher.exe actually reads from. Targeting
        // WOW6432Node explicitly makes the .reg file write to the same
        // location regardless of which bit-ness process imports it.
        string keyPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\DirectPlay\Applications\{EscapeRegKeyName(appName)}";

        var sb = new StringBuilder();
        // Windows Registry Editor Version 5.00 header is REQUIRED — without
        // it regedit treats the file as plain text and refuses to import it.
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine($"[{keyPath}]");
        sb.AppendLine($"\"Guid\"=\"{{{guid.ToString().ToLowerInvariant()}}}\"");
        sb.AppendLine($"\"Path\"=\"{EscapeRegValue(gamePath)}\"");
        sb.AppendLine($"\"File\"=\"{EscapeRegValue(gameExe)}\"");
        sb.AppendLine($"\"CommandLine\"=\"{EscapeRegValue(commandLine)}\"");
        sb.AppendLine($"\"CurrentDirectory\"=\"{EscapeRegValue(gamePath)}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the .reg file to disk and returns the path. Throws on I/O
    /// failure — callers should catch and surface that to the user, since
    /// this is a user-initiated "save" action, not a background operation.
    /// </summary>
    public static string WriteRegFile(string outputPath, string modTechnical, string gamePath, string gameExe, string? extraGameArgs = null)
    {
        string content = BuildRegFileContent(modTechnical, gamePath, gameExe, extraGameArgs);
        // .reg files are read by regedit as UTF-16LE with a BOM in modern
        // Windows, but plain ASCII/UTF-8-without-BOM also works for files
        // containing only ASCII content (no special characters in paths) —
        // using UTF-8 without BOM here for simplicity/portability, since our
        // generated content (paths, mod names) is expected to be ASCII in
        // the overwhelming majority of real installs. If a user's install
        // path contains non-ASCII characters, this may need revisiting.
        File.WriteAllText(outputPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return outputPath;
    }

    private static string EscapeRegKeyName(string name) =>
        // Key names in .reg files don't need backslash escaping (they're not
        // inside quotes), but should not themselves be empty or contain
        // characters invalid in registry key names ([ ] are structural here).
        name.Replace("[", "(").Replace("]", ")");

    private static string EscapeRegValue(string value) =>
        // .reg string values: backslash and double-quote must be escaped.
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
