using System.Diagnostics;
using CommandLine;
using Newtonsoft.Json;
using Obj2Tiles.Stages;

namespace Obj2Tiles
{
    internal class Program
    {
        private const string DefaultConfigFile = "config.json";
        
        private static async Task Main(string[] args)
        {
            var parserResult = await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(Run);

            if (parserResult.Tag == ParserResultType.NotParsed)
            {
                Console.WriteLine("Usage: obj2tiles [options]");
            }
        }

        private static async Task Run(Options options)
        {
            Console.WriteLine();
            Console.WriteLine(" *** OBJ to Tiles ***");
            Console.WriteLine();

            if (!TryGetConfig(options, out var config))
                return;

            if (Directory.Exists(config.Output))
            {
                Directory.Delete(config.Output, true);
            }

            Directory.CreateDirectory(config.Output);

            var pipelineId = Guid.NewGuid().ToString();
            var sw = new Stopwatch();
            var swg = Stopwatch.StartNew();

            Func<string, string> createTempFolder = s => CreateTempFolder(s, Path.Combine(config.Output, ".temp"));

            string? destFolderDecimation = null;
            string? destFolderSplit = null;

            try
            {
                destFolderDecimation = createTempFolder($"{pipelineId}-obj2tiles-decimation");
                Console.WriteLine($" => Decimation stage with {config.LODs.Length} LODs");
                sw.Start();
                var decimateRes = await StagesFacade.Decimate(config.Input, destFolderDecimation, config.LODs);
                Console.WriteLine(" ?> Decimation stage done in {0}", sw.Elapsed);
                Console.WriteLine();

                Console.WriteLine($" => Splitting stage with {config.MaxVerticesPerTile} vertices per tile");
                destFolderSplit = createTempFolder($"{pipelineId}-obj2tiles-split");
                
                var meshes = await StagesFacade.Split(decimateRes.DestFiles, destFolderSplit, config.MaxVerticesPerTile,
                    decimateRes.Bounds, config.PackingThreshold, config.LODs, config.KeepOriginalTextures, config.KtxQuality,
                    config.KtxCompressionLevel);

                Console.WriteLine(" ?> Splitting stage done in {0}", sw.Elapsed);
                Console.WriteLine();

                if (!config.KeepOriginalTextures)
                {
                    sw.Restart();
                    Console.WriteLine(" ?> Compressing png to ktx2");
                    await StagesFacade.Compress(meshes, config.KtxQuality, config.KtxCompressionLevel);
                    Console.WriteLine(" ?> Compressing done in {0}", sw.Elapsed);
                    Console.WriteLine();
                }

                sw.Restart();
                Console.WriteLine(" ?> Converting to glb");
                StagesFacade.Convert(destFolderSplit, config.Output, config.LODs);
                Console.WriteLine(" ?> Converting done in {0}", sw.Elapsed);
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine(" !> Exception:");
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine(" => Pipeline completed in {0}", swg.Elapsed);

                var tmpFolder = Path.Combine(config.Output, ".temp");

                if (config.KeepIntermediateFiles)
                {
                    Console.WriteLine(
                        $" ?> Skipping cleanup, intermediate files are in '{tmpFolder}' with pipeline id '{pipelineId}'");

                    Console.WriteLine(" ?> You should delete this folder manually, it is only for debugging purposes");
                }
                else
                {
                    Console.WriteLine(" => Cleaning up");

                    if (destFolderDecimation != null && destFolderDecimation != config.Output)
                        Directory.Delete(destFolderDecimation, true);

                    if (destFolderSplit != null && destFolderSplit != config.Output)
                        Directory.Delete(destFolderSplit, true);

                    if (Directory.Exists(tmpFolder))
                        Directory.Delete(tmpFolder, true);

                    Console.WriteLine(" ?> Cleaning up ok");
                }
            }
        }

        private static bool TryGetConfig(Options options, out AppConfig config)
        {
            config = null;
            var configPath = string.IsNullOrEmpty(options.Config) ? DefaultConfigFile : options.Config;

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Config file not found: {configPath}");
                return false;
            }

            using (var reader = File.OpenText(configPath))
            using (var jsonReader = new JsonTextReader(reader))
            {
                config = JsonSerializer.CreateDefault().Deserialize<AppConfig>(jsonReader);
            }

            if (config == null)
            {
                Console.WriteLine($"Config file error!");
                return false;
            }

            var input = string.IsNullOrEmpty(options.Input) ? config.Input : options.Input;
            var output = string.IsNullOrEmpty(options.Output) ? config.Output : options.Output;

            config.Input = Path.GetFullPath(input);
            config.Output = Path.GetFullPath(output);
            return true;
        }

        private static string CreateTempFolder(string folderName, string baseFolder)
        {
            var tempFolder = Path.Combine(baseFolder, folderName);
            Directory.CreateDirectory(tempFolder);
            return tempFolder;
        }
    }
}