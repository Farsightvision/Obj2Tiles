﻿namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static void Convert(string sourcePath, string destPath, LodConfig[] lods)
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

        Parallel.ForEach(filesToConvert, (file) =>
        {
            Console.WriteLine($" -> Converting to Glb '{file.Item1}'");
            Utils.ConvertGlb(file.Item1, file.Item2);
        });
    }
}