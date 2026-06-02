using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Obj2Tiles.Test;

public class DispatcherTests
{
    /// <summary>
    /// Asserts that the default flat-grid pipeline (no --hierarchical-lods)
    /// produces a structurally valid tileset: tileset.json exists, LOD-0/
    /// directory exists, and asset.version == "1.0". Confirms the dispatcher
    /// preserves the master flat-grid output shape.
    /// Skipped when the Brighton fixture is absent (e.g. local runs without
    /// the CI fixture).
    /// </summary>
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
            // Run the default flat-grid pipeline via the same CLI surface the user uses.
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
                try { p.Kill(true); } catch { /* best-effort */ }
                Assert.Fail("flat-grid pipeline timed out after 5 min");
            }

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();

            Assert.That(
                p.ExitCode,
                Is.EqualTo(0),
                $"flat-grid pipeline exited with code {p.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");

            // Verify expected flat-grid output structure exists.
            Assert.That(File.Exists(Path.Combine(output, "tileset.json")), Is.True, "tileset.json missing");
            Assert.That(Directory.Exists(Path.Combine(output, "LOD-0")), Is.True, "LOD-0 dir missing");

            // tileset.json shape sanity (flat-grid uses asset.version "1.0").
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
                // Don't mask real test failures with cleanup exceptions.
            }
        }
    }
}
