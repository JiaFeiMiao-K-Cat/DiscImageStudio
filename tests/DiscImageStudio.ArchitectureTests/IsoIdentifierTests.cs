using System.Buffers.Binary;
using System.IO;
using System.Text;
using DiscImageStudio.Iso;

internal static class IsoIdentifierTests
{
    private const string LongStem = "A photo taken on a very long summer holiday by the sea in the year 2025";

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"iso-identifiers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] photo = Enumerable.Range(0, 3001).Select(index => (byte)(index * 13)).ToArray();
            File.WriteAllBytes(Path.Combine(directory, LongStem + ".jpg"), photo);
            File.WriteAllBytes(Path.Combine(directory, LongStem + " (copy).jpg"), [1]);
            File.WriteAllBytes(Path.Combine(directory, "a-b.pdf"), [2]);
            File.WriteAllBytes(Path.Combine(directory, "a_b.pdf"), [3]);
            File.WriteAllBytes(Path.Combine(directory, "root.bin"), [4]);
            File.WriteAllBytes(Path.Combine(directory, new string('x', 70) + ".longextension"), [5]);
            File.WriteAllBytes(Path.Combine(directory, ".hidden"), [6]);

            var plan = Iso9660ImageWriter.Plan(directory, 256, "NAMES", DateTimeOffset.UnixEpoch);
            using MemoryStream output = new();
            Iso9660ImageWriter.WriteSequentialPrefix(plan, output, 256);
            byte[] image = output.ToArray();

            Dictionary<string, byte[]> joliet = ReadRoot(image, 17, joliet: true);
            Dictionary<string, byte[]> primary = ReadRoot(image, 16, joliet: false);
            Require(joliet.Count == 7 && primary.Count == 7, "every file keeps a distinct identifier in both trees");
            Require(joliet.Keys.All(name => name.Length <= 60) && primary.Keys.All(name => name.Length <= 31),
                "identifiers, including the ';1' version, stay within the Joliet and ISO9660 limits");

            Require(joliet.ContainsKey("root.bin;1") && primary.ContainsKey("ROOT.BIN;1"),
                "names that already fit are unchanged");
            Require(joliet.ContainsKey(".hidden;1") && joliet.ContainsKey("a-b.pdf;1") && joliet.ContainsKey("a_b.pdf;1"),
                "Joliet keeps short names verbatim, including a leading dot");

            string[] longPhotos = joliet.Keys.Where(name => name.StartsWith("A photo", StringComparison.Ordinal)).ToArray();
            Require(longPhotos.Length == 2 && longPhotos.All(name => name.EndsWith(".jpg;1", StringComparison.Ordinal)),
                $"shortened long Joliet names keep their extension: {string.Join(" | ", longPhotos)}");
            Require(longPhotos.Count(name => name.EndsWith("~2.jpg;1", StringComparison.Ordinal)) == 1,
                "a colliding shortened name is disambiguated before its extension");
            Require(longPhotos.Count(name => joliet[name].SequenceEqual(photo)) == 1, "the renamed file's contents are intact");

            string[] primaryPhotos = primary.Keys.Where(name => name.StartsWith("A_PHOTO", StringComparison.Ordinal)).ToArray();
            Require(primaryPhotos.Length == 2 && primaryPhotos.All(name => name.EndsWith(".JPG;1", StringComparison.Ordinal)),
                $"shortened ISO9660 names keep their extension: {string.Join(" | ", primaryPhotos)}");

            Require(primary.ContainsKey("A_B.PDF;1") && primary.ContainsKey("A_B~2.PDF;1"),
                "names that collide after ISO9660 normalization keep their extension");

            Require(joliet.Keys.Any(name => name.StartsWith("xxxx", StringComparison.Ordinal) && !name.Contains(".longextension")),
                "an extension too long to preserve falls back to plain truncation");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("iso-identifiers: passed (long names keep extensions, collisions before extensions, limits, unchanged short names)");
    }

    // Reads only the root directory named by the volume descriptor in the given sector.
    private static Dictionary<string, byte[]> ReadRoot(byte[] image, int descriptorSector, bool joliet)
    {
        int descriptor = descriptorSector * 2048;
        Require(image.AsSpan(descriptor + 1, 5).SequenceEqual("CD001"u8), "volume descriptor signature");
        int root = descriptor + 156;
        int begin = checked((int)ReadBoth(image, root + 2) * 2048);
        int end = checked(begin + (int)ReadBoth(image, root + 10));
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        for (int offset = begin; offset < end;)
        {
            int length = image[offset];
            if (length == 0) { offset = (offset / 2048 + 1) * 2048; continue; }
            int identifierLength = image[offset + 32];
            if (identifierLength > 1)
            {
                string name = joliet
                    ? Encoding.BigEndianUnicode.GetString(image, offset + 33, identifierLength)
                    : Encoding.ASCII.GetString(image, offset + 33, identifierLength);
                int position = checked((int)ReadBoth(image, offset + 2) * 2048);
                int count = checked((int)ReadBoth(image, offset + 10));
                Require(files.TryAdd(name, image.AsSpan(position, count).ToArray()), $"duplicate identifier {name}");
            }
            offset += length;
        }
        return files;
    }

    private static uint ReadBoth(byte[] image, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
        Require(value == BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset + 4, 4)), "dual-endian field consistency");
        return value;
    }

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
