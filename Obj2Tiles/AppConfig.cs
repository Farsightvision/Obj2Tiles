namespace Obj2Tiles;

public class AppConfig
{
    public string Input { get; set; }
    public string Output { get; set; }
    public int MaxVerticesPerTile { get; set; }
    public double PackingThreshold { get; set; }
    public bool KeepIntermediateFiles { get; set; }
    public LodConfig[] LODs { get; set; }
    public int ThreadsCount { get; set; }
    public int MaxTotalAtlasArea { get; set; }
    public double BaseError { get; set; } = 100.0;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double Altitude { get; set; } = 0;
    public double Scale { get; set; } = 1;
    public bool YUpToZUp { get; set; } = false;
}

public class LodConfig
{
    public float Quality { get; set; }
    public bool SaveVertexColor { get; set; }
    public bool SaveUv { get; set; }
}