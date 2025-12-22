using System.Diagnostics;
using CommandLine;
using Newtonsoft.Json;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;
using Obj2Tiles.Stages.Model;

namespace Obj2Tiles
{
    internal class Program
    {
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
            
            Console.WriteLine(JsonConvert.SerializeObject(config));
            Console.WriteLine();

            if (Directory.Exists(config.Output))
            {
                Directory.Delete(config.Output, true);
            }

            Directory.CreateDirectory(config.Output);

            var pipelineId = Guid.NewGuid().ToString();
            var sw = new Stopwatch();
            var swg = Stopwatch.StartNew();
            var tempFolder = Path.Combine(config.Output, ".temp");

            string? destFolderDecimation = null;
            string? destFolderSplit = null;

            try
            {
                destFolderDecimation = CreateTempFolder($"{pipelineId}-obj2tiles-decimation", tempFolder);
                Console.WriteLine($" => Decimation stage with {config.LODs.Length} LODs");
                sw.Start();
                var decimateRes = StagesFacade.Decimate(config.Input, destFolderDecimation, config.LODs);
                Console.WriteLine(" ?> Decimation stage done in {0}", sw.Elapsed);
                Console.WriteLine();

                Console.WriteLine($" => Splitting stage with {config.MaxVerticesPerTile} vertices per tile");
                destFolderSplit = CreateTempFolder($"{pipelineId}-obj2tiles-split", tempFolder);
                
                var meshes = StagesFacade.Split(decimateRes.DestFiles, destFolderSplit,
                    config.MaxVerticesPerTile, decimateRes.Bounds, config.PackingThreshold, config.LODs, config.ThreadsCount, config.MaxTotalAtlasArea);

                Console.WriteLine(" ?> Splitting stage done in {0}", sw.Elapsed);
                Console.WriteLine();

                if (config.UseKtxTextures)
                {
                    sw.Restart();
                    Console.WriteLine(" ?> Compressing png to ktx2");
                    await StagesFacade.Compress(meshes, config.ThreadsCount);
                    Console.WriteLine(" ?> Compressing done in {0}", sw.Elapsed);
                    Console.WriteLine();
                }

                sw.Restart();
                Console.WriteLine(" ?> Converting to glb");
                await StagesFacade.Convert(destFolderSplit, config.Output, config.LODs, config.ThreadsCount);
                Console.WriteLine(" ?> Converting done in {0}", sw.Elapsed);
                Console.WriteLine();

                GenerateTileset(sw, meshes, config);

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

            if (!string.IsNullOrEmpty(options.Config))
            {
                using (var reader = File.OpenText(options.Config))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    config = JsonSerializer.CreateDefault().Deserialize<AppConfig>(jsonReader);
                }

                return true;
            }

            if (string.IsNullOrEmpty(options.Input))
            {
                Console.WriteLine("Input parameter missing!");
                return false;
            }
            
            if (string.IsNullOrEmpty(options.Output))
            {
                Console.WriteLine("Output parameter missing!");
                return false;
            }

            config = new AppConfig
            {
                Input = options.Input,
                Output = options.Output,
                MaxVerticesPerTile = options.MaxVerticesPerTile,
                PackingThreshold = options.PackingThreshold,
                ThreadsCount = options.ThreadsCount,
                MaxTotalAtlasArea = options.MaxTotalAtlasArea,
                KeepIntermediateFiles = options.KeepIntermediateFiles,
                UseKtxTextures = options.UseKtxTextures,
                BaseError = options.BaseError,
                Latitude = options.Latitude,
                Longitude = options.Longitude,
                Altitude = options.Altitude,
                Scale = options.Scale,
                YUpToZUp = options.YUpToZUp,
                LODs = JsonConvert.DeserializeObject<LodConfig[]>(options.LODs)
            };
            
            return true;
        }

        private static string CreateTempFolder(string folderName, string baseFolder)
        {
            var tempFolder = Path.Combine(baseFolder, folderName);
            Directory.CreateDirectory(tempFolder);
            return tempFolder;
        }

        private static void GenerateTileset(Stopwatch sw, Dictionary<LodConfig, List<IMesh>> meshes, AppConfig config)
        {
            sw.Restart();
            Console.WriteLine(" ?> Generating tileset.json");

            var boundsMapper = TilesetGenerationHelper.PrepareBoundsMapper(meshes, config.LODs);
            var coords = TilesetGenerationHelper.CreateGpsCoords(
                config.Latitude, config.Longitude, config.Altitude, config.Scale, config.YUpToZUp);

            StagesFacade.Tile(config.Output, config.LODs.Length, config.BaseError, boundsMapper, coords);

            Console.WriteLine(" ?> Tileset generation done in {0}", sw.Elapsed);
            Console.WriteLine();
        }
    }
}