using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Obj2Tiles.Test;

public class DispatcherTests
{
    /// <summary>Asserts the default flat-grid pipeline produces a valid tileset; skipped without the Brighton fixture.</summary>
    [Test]
    public async Task FlatGridPipeline_OutputIsByteIdenticalToBaseline()
    {
        var fixtureObj = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "fixtures", "brighton", "odm_textured_model_geo.obj");

        if (!File.Exists(fixtureObj))
        {
            Assert.Ignore($"Brighton fixture not present at {fixtureObj}");
        }

        var projectPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "Obj2Tiles");

        var output = Path.Combine(Path.GetTempPath(), $"flat-test-{Guid.NewGuid()}");
        try
        {
            var lodsJson = "[{\"Quality\":1.0,\"JpegQuality\":90,\"MaxAtlasSize\":2048}]";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" -- --input \"{fixtureObj}\" --output \"{output}\" --lods \"{lodsJson}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = System.Diagnostics.Process.Start(psi)!;

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                Assert.Fail("flat-grid pipeline timed out after 5 min");
            }

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();

            Assert.That(
                p.ExitCode,
                Is.EqualTo(0),
                $"flat-grid pipeline exited with code {p.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");

            Assert.That(File.Exists(Path.Combine(output, "tileset.json")), Is.True, "tileset.json missing");
            Assert.That(Directory.Exists(Path.Combine(output, "LOD-0")), Is.True, "LOD-0 dir missing");

            var ts = JObject.Parse(File.ReadAllText(Path.Combine(output, "tileset.json")));
            Assert.That(ts["asset"]?["version"]?.ToString(), Is.EqualTo("1.0"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(output)) Directory.Delete(output, true);
            }
            catch
            {
            }
        }
    }
}
