namespace TafClient.Service;

/// <summary>
/// Handles installing a mod folder into the Wine prefix on macOS/Linux —
/// copying the user's selected source folder (their existing TA install,
/// wherever it lives on the real filesystem) into {WINEPREFIX}/drive_c/TAF/{mod},
/// so that:
///   - Wine launching TotalA.exe sees a normal C:\TAF\{mod}\TotalA.exe path,
///     exactly as it would on real Windows — no path translation surprises.
///   - Our own code (MapTool, MapService's map scanning, TAForever.ini
///     writing) keeps working against the real filesystem path, unchanged.
///
/// Only meaningful on macOS/Linux — on Windows there's no Wine prefix at
/// all, so "Install Mod" there just means picking the existing install
/// folder directly (see HostGameDialog.InstallSelectedMod, which already
/// handles that case via ExePathDialog with no copying involved).
/// </summary>
public static class ModInstallService
{
    /// <summary>
    /// Copies sourceFolder's contents into {drive_c}/TAF/{modTechnical},
    /// reporting progress as (filesCopied, totalFiles). Returns the
    /// destination's real filesystem path on success.
    ///
    /// Skips files that already exist with the same size and a not-older
    /// modified time at the destination, so re-running this after an
    /// interrupted copy or to pick up a few changed files doesn't recopy
    /// everything — useful given some TA mod folders observed this session
    /// have 1000+ map files.
    /// </summary>
    public static async Task<string> InstallAsync(
        string sourceFolder, string modTechnical,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder does not exist: {sourceFolder}");

        string destRoot = Path.Combine(WineDetector.GetWineDriveC(), "TAF", modTechnical);
        Directory.CreateDirectory(destRoot);

        var allFiles = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList();
        int total = allFiles.Count;
        int done = 0;

        foreach (var srcFile in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(sourceFolder, srcFile);
            string destFile = Path.Combine(destRoot, relative);
            string? destDir = Path.GetDirectoryName(destFile);
            if (destDir is not null) Directory.CreateDirectory(destDir);

            if (ShouldSkip(srcFile, destFile))
            {
                done++;
                progress?.Report((done, total));
                continue;
            }

            // Copy via a temp file + move rather than a direct overwrite, so
            // a cancellation or crash mid-copy can't leave a half-written
            // file sitting at the real destination path looking complete.
            string tempFile = destFile + ".tafcopy-tmp";
            await using (var src = File.OpenRead(srcFile))
            await using (var dst = File.Create(tempFile))
            {
                await src.CopyToAsync(dst, ct);
            }
            File.Move(tempFile, destFile, overwrite: true);

            done++;
            progress?.Report((done, total));
        }

        return destRoot;
    }

    private static bool ShouldSkip(string srcFile, string destFile)
    {
        if (!File.Exists(destFile)) return false;
        var srcInfo = new FileInfo(srcFile);
        var dstInfo = new FileInfo(destFile);
        return srcInfo.Length == dstInfo.Length && dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc;
    }
}
