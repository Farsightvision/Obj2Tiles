namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task Convert(string sourcePath, string destPath, LodConfig[] lods, int threadsCount)
    {
        var filesToConvert = new List<Tuple<string, string>>();

        for (var index = 0; index < lods.Length; index++)
        {
            var lod = lods[index];
            var files = Directory.GetFiles(Path.Combine(sourcePath, "LOD-" + index), "*.obj");

            foreach (var file in files)
            {
                var outputFolder = Path.Combine(destPath, "LOD-" + index);
                Directory.CreateDirectory(outputFolder);
                filesToConvert.Add(new Tuple<string, string>(file, outputFolder));
            }
        }

        var semaphore = new SemaphoreSlim(threadsCount);
        var tasks = new List<Task>();

        foreach (var file in filesToConvert)
        {
            tasks.Add(ConvertFile(file, semaphore));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task ConvertFile(Tuple<string, string> file, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();

        try
        {
            Console.WriteLine($" -> Converting to Glb '{file.Item1}'");
            Utils.ConvertGlb(file.Item1, file.Item2);
        }
        finally
        {
            semaphore.Release();
        }
    }
}