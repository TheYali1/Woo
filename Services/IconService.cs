using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Woo_.Services;

public sealed class IconService
{
    private static readonly int[] IconSizes = [16, 32, 48, 256];

    public async Task<string> CreateIcoAsync(string sourcePath, string outputIcoPath, string? outputPngPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputIcoPath)!);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Icon source file was not found.", sourcePath);
        }

        try
        {
            using var image = await Image.LoadAsync(sourcePath);
            await WriteIcoAsync(image, outputIcoPath);

            if (!string.IsNullOrWhiteSpace(outputPngPath))
            {
                await SavePngPreviewAsync(image, outputPngPath);
            }
        }
        catch when (Path.GetExtension(sourcePath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            var tempPngPath = Path.Combine(Path.GetTempPath(), $"woo-icon-{Guid.NewGuid():N}.png");
            try
            {
                if (TryExtractPngFromIco(bytes, tempPngPath))
                {
                    await CreateIcoAsync(tempPngPath, outputIcoPath, outputPngPath);
                }
                else
                {
                    File.Copy(sourcePath, outputIcoPath, true);
                    if (!string.IsNullOrWhiteSpace(outputPngPath))
                    {
                        var fallbackPng = Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png");
                        if (File.Exists(fallbackPng))
                        {
                            File.Copy(fallbackPng, outputPngPath, true);
                        }
                    }
                }
            }
            finally
            {
                TryDelete(tempPngPath);
            }
        }

        return outputIcoPath;
    }

    public async Task<string> CreateIcoFromBytesAsync(byte[] bytes, string outputIcoPath, string? outputPngPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputIcoPath)!);

        if (IsIco(bytes))
        {
            var tempPngPath = Path.Combine(Path.GetTempPath(), $"woo-icon-{Guid.NewGuid():N}.png");
            try
            {
                if (TryExtractPngFromIco(bytes, tempPngPath))
                {
                    return await CreateIcoAsync(tempPngPath, outputIcoPath, outputPngPath);
                }

                await File.WriteAllBytesAsync(outputIcoPath, bytes);
                if (!string.IsNullOrWhiteSpace(outputPngPath))
                {
                    TryExtractPngFromIco(bytes, outputPngPath);
                }

                return outputIcoPath;
            }
            finally
            {
                TryDelete(tempPngPath);
            }
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"woo-icon-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tempPath, bytes);

        try
        {
            return await CreateIcoAsync(tempPath, outputIcoPath, outputPngPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task WriteIcoAsync(Image image, string outputPath)
    {
        var entries = new List<(int Size, byte[] Data)>();
        foreach (var size in IconSizes)
        {
            using var resized = image.Clone(context => context.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Pad,
                PadColor = Color.Transparent
            }));

            await using var memory = new MemoryStream();
            await resized.SaveAsPngAsync(memory, new PngEncoder());
            entries.Add((size, memory.ToArray()));
        }

        await using var output = File.Create(outputPath);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Count);

        var imageOffset = 6 + entries.Count * 16;
        foreach (var entry in entries)
        {
            writer.Write((byte)(entry.Size == 256 ? 0 : entry.Size));
            writer.Write((byte)(entry.Size == 256 ? 0 : entry.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)imageOffset);
            imageOffset += entry.Data.Length;
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Data);
        }
    }

    private static async Task SavePngPreviewAsync(Image image, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var resized = image.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(256, 256),
            Mode = ResizeMode.Pad,
            PadColor = Color.Transparent
        }));

        await resized.SaveAsPngAsync(outputPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool IsIco(byte[] bytes)
    {
        return bytes.Length >= 6 &&
               bytes[0] == 0 &&
               bytes[1] == 0 &&
               bytes[2] == 1 &&
               bytes[3] == 0;
    }

    private static bool TryExtractPngFromIco(byte[] bytes, string outputPath)
    {
        try
        {
            var count = BitConverter.ToUInt16(bytes, 4);
            if (count == 0)
            {
                return false;
            }

            var bestOffset = 0;
            var bestSize = 0;
            var bestPixels = -1;
            var bestDibOffset = 0;
            var bestDibSize = 0;
            var bestDibPixels = -1;

            for (var index = 0; index < count; index++)
            {
                var entryOffset = 6 + index * 16;
                if (entryOffset + 16 > bytes.Length)
                {
                    continue;
                }

                var width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
                var height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
                var size = BitConverter.ToInt32(bytes, entryOffset + 8);
                var offset = BitConverter.ToInt32(bytes, entryOffset + 12);

                if (offset < 0 || size <= 8 || offset + size > bytes.Length)
                {
                    continue;
                }

                var pixels = width * height;
                if (IsPng(bytes, offset))
                {
                    if (pixels > bestPixels)
                    {
                        bestPixels = pixels;
                        bestOffset = offset;
                        bestSize = size;
                    }

                    continue;
                }

                if (pixels > bestDibPixels)
                {
                    bestDibPixels = pixels;
                    bestDibOffset = offset;
                    bestDibSize = size;
                }
            }

            if (bestSize > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, bytes.AsSpan(bestOffset, bestSize).ToArray());
                return true;
            }

            if (bestDibSize > 0)
            {
                return TryWriteDibPng(bytes, bestDibOffset, bestDibSize, outputPath);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteDibPng(byte[] bytes, int offset, int size, string outputPath)
    {
        try
        {
            if (offset + 40 > bytes.Length || size < 40)
            {
                return false;
            }

            var headerSize = BitConverter.ToInt32(bytes, offset);
            var width = BitConverter.ToInt32(bytes, offset + 4);
            var dibHeight = BitConverter.ToInt32(bytes, offset + 8);
            var bitDepth = BitConverter.ToUInt16(bytes, offset + 14);
            var compression = BitConverter.ToInt32(bytes, offset + 16);
            var height = Math.Abs(dibHeight) / 2;

            if (headerSize < 40 || width <= 0 || height <= 0 || bitDepth != 32 || compression != 0)
            {
                return false;
            }

            var pixelOffset = offset + headerSize;
            var required = width * height * 4;
            if (pixelOffset + required > offset + size || pixelOffset + required > bytes.Length)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var image = new Image<Rgba32>(width, height);
            for (var y = 0; y < height; y++)
            {
                var sourceY = height - 1 - y;
                for (var x = 0; x < width; x++)
                {
                    var pixelIndex = pixelOffset + (sourceY * width + x) * 4;
                    image[x, y] = new Rgba32(
                        bytes[pixelIndex + 2],
                        bytes[pixelIndex + 1],
                        bytes[pixelIndex],
                        bytes[pixelIndex + 3]);
                }
            }

            image.SaveAsPng(outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPng(byte[] bytes, int offset)
    {
        return offset + 8 <= bytes.Length &&
               bytes[offset] == 0x89 &&
               bytes[offset + 1] == 0x50 &&
               bytes[offset + 2] == 0x4E &&
               bytes[offset + 3] == 0x47 &&
               bytes[offset + 4] == 0x0D &&
               bytes[offset + 5] == 0x0A &&
               bytes[offset + 6] == 0x1A &&
               bytes[offset + 7] == 0x0A;
    }
}
