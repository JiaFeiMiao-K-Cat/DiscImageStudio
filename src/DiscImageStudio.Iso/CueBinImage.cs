using System.Text;
using System.Text.RegularExpressions;

namespace DiscImageStudio.Iso;

public sealed record CueBinImageInfo(string CuePath, IReadOnlyList<string> BinaryFiles, long TotalBinaryBytes);

/// <summary>Validates supported binary CUE sheets and exports their referenced track files unchanged.</summary>
public static partial class CueBinImage
{
    private static readonly HashSet<string> SupportedTrackModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIO",
        "MODE1/2048",
        "MODE1/2352",
        "MODE2/2336",
        "MODE2/2352",
        "CDI/2336",
        "CDI/2352",
    };

    [GeneratedRegex(@"^\s*FILE\s+(?:""([^""]+)""|(\S+))\s+(\S+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileLineRegex();

    [GeneratedRegex(@"^\s*TRACK\s+\d+\s+(\S+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackLineRegex();

    [GeneratedRegex(@"^\s*INDEX\s+01\s+\d{2}:\d{2}:\d{2}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexOneRegex();

    public static CueBinImageInfo Inspect(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);
        string fullCuePath = Path.GetFullPath(cuePath);
        if (!Path.GetExtension(fullCuePath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the .cue sheet, not a referenced binary track.", nameof(cuePath));
        if (!File.Exists(fullCuePath)) throw new FileNotFoundException("The selected CUE sheet does not exist.", fullCuePath);

        string[] lines = File.ReadAllLines(fullCuePath);
        string cueDirectory = Path.GetDirectoryName(fullCuePath)!;
        List<string> references = [];
        HashSet<string> uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        bool hasTrack = false;
        bool currentTrackHasIndexOne = true;
        foreach (string line in lines)
        {
            Match fileMatch = FileLineRegex().Match(line);
            if (fileMatch.Success)
            {
                string fileType = fileMatch.Groups[3].Value;
                if (!fileType.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"CUE FILE type '{fileType}' is unsupported; only BINARY is supported.");
                string reference = fileMatch.Groups[1].Success
                    ? fileMatch.Groups[1].Value
                    : fileMatch.Groups[2].Value;
                if (Path.IsPathRooted(reference))
                    throw new InvalidDataException("CUE files must refer to binary tracks with relative paths.");
                string fullBinaryPath = Path.GetFullPath(Path.Combine(cueDirectory, reference));
                if (!IsWithinDirectory(fullBinaryPath, cueDirectory))
                    throw new InvalidDataException("CUE binary tracks must remain within the CUE file's directory.");
                if (!File.Exists(fullBinaryPath))
                    throw new FileNotFoundException($"CUE binary track was not found: {reference}", fullBinaryPath);
                references.Add(fullBinaryPath);
                uniquePaths.Add(fullBinaryPath);
                continue;
            }

            Match trackMatch = TrackLineRegex().Match(line);
            if (trackMatch.Success)
            {
                if (hasTrack && !currentTrackHasIndexOne)
                    throw new InvalidDataException("Every CUE track must define INDEX 01.");
                string mode = trackMatch.Groups[1].Value;
                if (!SupportedTrackModes.Contains(mode))
                    throw new InvalidDataException($"CUE track mode '{mode}' is unsupported.");
                hasTrack = true;
                currentTrackHasIndexOne = false;
                continue;
            }

            if (hasTrack && IndexOneRegex().IsMatch(line))
                currentTrackHasIndexOne = true;
        }

        if (!hasTrack || !currentTrackHasIndexOne)
            throw new InvalidDataException("The CUE sheet must contain at least one track with INDEX 01.");
        if (references.Count == 0)
            throw new InvalidDataException("The CUE sheet does not reference any BINARY track files.");

        long totalBytes = 0;
        foreach (string binaryPath in uniquePaths)
            totalBytes = checked(totalBytes + new FileInfo(binaryPath).Length);

        return new(fullCuePath, uniquePaths.ToArray(), totalBytes);
    }

    public static string Export(string cuePath, string outputCuePath, CancellationToken cancellationToken = default)
    {
        CueBinImageInfo info = Inspect(cuePath);
        string outputCue = Path.GetFullPath(outputCuePath);
        if (!Path.GetExtension(outputCue).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CUE/BIN output must use the .cue extension.", nameof(outputCuePath));
        if (outputCue.Equals(info.CuePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output CUE sheet must not overwrite the source.", nameof(outputCuePath));

        string[] lines = File.ReadAllLines(info.CuePath);
        string sourceDirectory = Path.GetDirectoryName(info.CuePath)!;
        string outputDirectory = Path.GetDirectoryName(outputCue)!;
        string outputStem = Path.GetFileNameWithoutExtension(outputCue);
        Dictionary<string, string> renamedFiles = new(StringComparer.OrdinalIgnoreCase);
        List<string> stagedFiles = [];
        List<string> publishedFiles = [];
        string tempCue = outputCue + $".{Guid.NewGuid():N}.partial";
        int nextFile = 0;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                Match match = FileLineRegex().Match(lines[lineIndex]);
                if (!match.Success) continue;

                string reference = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                string source = Path.GetFullPath(Path.Combine(sourceDirectory, reference));
                if (!renamedFiles.TryGetValue(source, out string? outputName))
                {
                    outputName = $"{outputStem}-{++nextFile:00}.bin";
                    renamedFiles.Add(source, outputName);
                    string destination = Path.Combine(outputDirectory, outputName);
                    string stagedPath = destination + $".{Guid.NewGuid():N}.partial";
                    using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                        }
                        output.Flush(flushToDisk: true);
                    }
                    stagedFiles.Add(stagedPath);
                }

                lines[lineIndex] = $"FILE \"{outputName}\" BINARY";
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (FileStream output = new(tempCue, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (string line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine(line);
                }
            }

            for (int index = 0; index < stagedFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(outputDirectory, $"{outputStem}-{index + 1:00}.bin");
                if (File.Exists(destination))
                    throw new IOException($"CUE export would overwrite an existing track file: {destination}");
                File.Move(stagedFiles[index], destination);
                publishedFiles.Add(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempCue, outputCue, overwrite: true);
            return outputCue;
        }
        catch
        {
            foreach (string path in publishedFiles)
                if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally
        {
            if (File.Exists(tempCue)) File.Delete(tempCue);
            foreach (string path in stagedFiles)
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static bool IsWithinDirectory(string filePath, string directoryPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath))
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(filePath);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
