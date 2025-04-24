using CommandLine;

namespace Obj2Tiles;

public sealed class Options
{
    [Option("config", Required = false, HelpText = "Config file.")]
    public string Config { get; set; }
    
    [Option("input", Required = false, HelpText = "Input OBJ file.")]
    public string Input { get; set; }

    [Option("output", Required = false, HelpText = "Output folder.")]
    public string Output { get; set; }

    [Option("max-vertices", Required = false, HelpText = "Splitting by vertex count per tile. Approximate value", Default = 4000)]
    public int MaxVerticesPerTile { get; set; }
    
    [Option("packing-threshold", Required = false, HelpText = "Minimum fill ratio required to skip texture compression. If the atlas is less packed than this, compression is applied.", Default = 0.618)]
    public double PackingThreshold { get; set; }
    
    // [Option('l', "lods", Required = false, HelpText = "How many levels of details", Default = 3)]
    // public int LODs { get; set; }

    [Option('k', "keep-textures", Required = false, HelpText = "Keeps original textures", Default = false)]
    public bool KeepOriginalTextures { get; set; }
    
    // [Option("lat", Required = false, HelpText = "Latitude of the mesh", Default = null)]
    // public double? Latitude { get; set; }
    
    // [Option("lon", Required = false, HelpText = "Longitude of the mesh", Default = null)]
    // public double? Longitude { get; set; }
    
    // [Option("alt", Required = false, HelpText = "Altitude of the mesh (meters)", Default = 0)]
    // public double Altitude { get; set; }
    
    // [Option("scale", Required = false, HelpText = "Scale for data if using units other than meters ( 1200.0/3937.0 for survey ft)", Default = 1.0)]
    // public double Scale { get; set; }
    
    // [Option('e',"error", Required = false, HelpText = "Base error for root node", Default = 100.0)]
    // public double BaseError { get; set; }
    
    // [Option("use-system-temp", Required = false, HelpText = "Uses the system temp folder", Default = false)]
    // public bool UseSystemTempFolder { get; set; }
    
    [Option("keep-intermediate", Required = false, HelpText = "Keeps the intermediate files (do not cleanup)", Default = false)]
    public bool KeepIntermediateFiles { get; set; }
    
    [Option("threads", Required = false, HelpText = "Count threads for parallel ktx compression", Default = 8)]
    public int ThreadsCount { get; set; }
    
    [Option('l', "lods", Required = false, HelpText = "Lod's json")]
    public string LODs { get; set; }

    // [Option('t', "y-up-to-z-up", Required = false, HelpText = "Convert the upward Y-axis to the upward Z-axis, which is used in some situations where the upward axis may be the Y-axis or the Z-axis after the obj is exported.", Default = false)]
    // public bool YUpToZUp { get; set; }
}

// public enum Stage
// {
//     Decimation,
//     Splitting,
//     Tiling
// }