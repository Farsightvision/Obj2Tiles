using System.Diagnostics;
using System.Runtime.InteropServices;

public static class BasisuConverter
{
    private static readonly string ExecutablePath = GetBasisuExecutablePath();
    
    public static async Task ConvertPngToKtx2Async(byte quality, byte compression, string pngPath,
        string outputKtx2Path)
    {
        if (!File.Exists(pngPath))
            throw new FileNotFoundException("Input PNG not found", pngPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputKtx2Path)!);

        var args =
            $"-ktx2 -no_alpha -y_flip -q {quality} -comp_level {compression} \"{pngPath}\" -output_file \"{outputKtx2Path}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"basisu failed (code {process.ExitCode}):\n{stderr}");
        }

        Console.WriteLine($"basisu output:\n{stdout}");
    }

    public static void ConvertPngToKtx2(byte quality, byte compressionLevel, string input, string output)
    {
        ConvertPngToKtx2Async(quality, compressionLevel, input, output)
            .GetAwaiter()
            .GetResult();
    }
    
    private static string GetBasisuExecutablePath()
    {
        string basePath = "basisu";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"{basePath}.exe";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return basePath;

        throw new PlatformNotSupportedException("Unsupported OS for basisu conversion.");
    }
}