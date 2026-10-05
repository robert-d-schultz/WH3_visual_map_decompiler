using BitMiracle.LibTiff.Classic;

// Streams a float32 height TIFF row-by-row and reports mean |discrete Laplacian| - the
// same "roughness" metric used by the synthetic terrain tests/bench - so a
// HeightReconstructor change can be checked against real compiled map output, not just
// synthetic scenes. Usage: dotnet run -- <file1.tif> [file2.tif ...]

double Roughness(string path)
{
    using var tif = Tiff.Open(path, "r") ?? throw new IOException(path);
    int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
    int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

    var rows = new float[3][];
    for (int i = 0; i < 3; i++) rows[i] = new float[width];
    var scanline = new byte[tif.ScanlineSize()];

    void ReadRow(int y, float[] dst)
    {
        tif.ReadScanline(scanline, y);
        Buffer.BlockCopy(scanline, 0, dst, 0, width * 4);
    }

    ReadRow(0, rows[0]);
    ReadRow(1, rows[1]);

    double sum = 0;
    long n = 0;
    for (int y = 1; y < height - 1; y++)
    {
        ReadRow(y + 1, rows[(y + 1) % 3]);
        var prev = rows[(y - 1) % 3];
        var cur = rows[y % 3];
        var next = rows[(y + 1) % 3];
        for (int x = 1; x < width - 1; x++)
        {
            double lap = cur[x - 1] + cur[x + 1] + prev[x] + next[x] - 4 * cur[x];
            sum += Math.Abs(lap);
            n++;
        }
    }
    return sum / n;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage: RoughnessCheck <height.tif> [more.tif ...]");
    return 1;
}

foreach (var path in args)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double r = Roughness(path);
    Console.WriteLine($"{Path.GetFileName(path)}: roughness={r:F6} ({sw.Elapsed.TotalSeconds:F1}s)");
}
return 0;
