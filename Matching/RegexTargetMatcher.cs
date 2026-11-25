using System.Text.RegularExpressions;
using NasFileMonitor.Configuration;
using NasFileMonitor.Models;

namespace NasFileMonitor.Matching;

public sealed class RegexTargetMatcher : ITargetFileMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    public NasFileSnapshot? FindMatch(
        TrackedFileRecord sourceFile,
        IReadOnlyList<NasFileSnapshot> targetSnapshot,
        FolderCheckConfig config)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile.FileName);
        var ext = Path.GetExtension(sourceFile.FileName);

        var pattern = BuildPattern(sourceFile.FileName, baseName, ext, config);
        if (pattern is null)
            return null;

        var options = RegexOptions.CultureInvariant | (config.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(pattern, options, MatchTimeout);

        var minTargetTime = sourceFile.FirstSeenUtc.AddSeconds(config.MinTargetModifiedOffsetSeconds);
        var useBaseNamePrefilter = config.TargetFileNameRegex.Contains("{BaseName}", StringComparison.Ordinal)
                                   && string.IsNullOrWhiteSpace(config.SourceExtractRegex);

        var comparison = config.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        NasFileSnapshot? bestMatch = null;

        foreach (var candidate in targetSnapshot)
        {
            if (candidate.LastWriteTimeUtc < minTargetTime)
                continue;

            if (config.VerifySizeMatch && candidate.FileSizeBytes != sourceFile.FileSizeBytes)
                continue;

            if (useBaseNamePrefilter && !candidate.FileName.Contains(baseName, comparison))
                continue;

            try
            {
                if (!regex.IsMatch(candidate.FileName))
                    continue;
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (bestMatch is null || candidate.LastWriteTimeUtc > bestMatch.LastWriteTimeUtc)
                bestMatch = candidate;
        }

        return bestMatch;
    }

    private static string? BuildPattern(string fileName, string baseName, string ext, FolderCheckConfig config)
    {
        var pattern = config.TargetFileNameRegex
            .Replace("{FileName}", Regex.Escape(fileName), StringComparison.Ordinal)
            .Replace("{BaseName}", Regex.Escape(baseName), StringComparison.Ordinal)
            .Replace("{Ext}", Regex.Escape(ext), StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(config.SourceExtractRegex))
        {
            var extractOptions = RegexOptions.CultureInvariant | (config.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var extractRegex = new Regex(config.SourceExtractRegex, extractOptions, MatchTimeout);
            var match = extractRegex.Match(fileName);
            if (!match.Success)
                return null;

            foreach (var groupName in extractRegex.GetGroupNames())
            {
                if (int.TryParse(groupName, out _))
                    continue;

                var token = "{" + groupName + "}";
                if (pattern.Contains(token, StringComparison.Ordinal))
                    pattern = pattern.Replace(token, Regex.Escape(match.Groups[groupName].Value), StringComparison.Ordinal);
            }
        }

        return pattern;
    }
}
