using System.Buffers.Binary;
using System.Text;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>CD-TEXT beside an exported image: TITLE/PERFORMER lines in the CUE sheet and a
/// binary .cdt (the packs behind a 4-byte length header, as cdrecord textfile= and
/// libburn read it), so another burning program can write the same text.</summary>
public static class CdTextExport
{
    public static string WriteCdt(string path, CdTextAlbum album)
    {
        byte[] packs = CdTextPacks.Encode(album.Normalized()).Packs;
        byte[] file = new byte[4 + packs.Length];
        BinaryPrimitives.WriteUInt16BigEndian(file, checked((ushort)(packs.Length + 2)));
        packs.CopyTo(file, 4);
        File.WriteAllBytes(path, file);
        return path;
    }

    /// <summary>Rewrites a CUE sheet with disc and track TITLE/PERFORMER and a CDTEXTFILE
    /// reference. Existing FILE/TRACK/INDEX lines are kept as they are.</summary>
    public static void AddToCue(string cuePath, CdTextAlbum album, string? cdtPath)
    {
        album = album.Normalized();
        string[] lines = File.ReadAllLines(cuePath);
        StringBuilder cue = new();
        if (cdtPath is not null) cue.Append($"CDTEXTFILE \"{Path.GetFileName(cdtPath)}\"\r\n");
        Line(cue, "", "TITLE", album.Title);
        Line(cue, "", "PERFORMER", album.Performer);
        int track = 0;
        foreach (string line in lines)
        {
            cue.Append(line).Append("\r\n");
            if (line.TrimStart().StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase) && track < album.Tracks.Count)
            {
                Line(cue, "    ", "TITLE", album.Tracks[track].Title);
                Line(cue, "    ", "PERFORMER", album.Tracks[track].Performer);
                track++;
            }
        }
        File.WriteAllText(cuePath, cue.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void Line(StringBuilder cue, string indent, string keyword, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        cue.Append(indent).Append(keyword).Append(" \"").Append(value.Replace('"', '\'')).Append("\"\r\n");
    }
}
