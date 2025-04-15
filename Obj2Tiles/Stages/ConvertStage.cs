namespace Obj2Tiles.Stages;

public static class ConvertFacade
{
    public static void Convert(string sourcePath, string destPath, int lods)
    {
        var filesToConvert = new List<Tuple<string, string, bool, bool>>();

        for (var lod = 0; lod < lods; lod++)
        {
            var files = Directory.GetFiles(Path.Combine(sourcePath, "LOD-" + lod), "*.obj");

            foreach (var file in files)
            {
                var outputFolder = Path.Combine(destPath, "LOD-" + lod);
                Directory.CreateDirectory(outputFolder);
                var saveUv = lod == 0;
                filesToConvert.Add(new Tuple<string, string, bool, bool>(file, outputFolder, !saveUv, saveUv));
            }
        }

        Parallel.ForEach(filesToConvert, (file) =>
        {
            Console.WriteLine($" -> Converting to Glb '{file.Item1}'");
            Utils.ConvertGlb(file.Item1, file.Item2, file.Item3, file.Item4);
        });
    }
}