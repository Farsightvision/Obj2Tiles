namespace Obj2Tiles;

public class AppConfig
{
    public string Input { get; set; }
    public string Output { get; set; }
    public int MaxVerticesPerTile { get; set; }
    public double PackingThreshold { get; set; }
    public bool KeepOriginalTextures { get; set; }
    public bool KeepIntermediateFiles { get; set; }
    public LodConfig[] LODs { get; set; }
    public byte KtxQuality { get; set; }
    public byte KtxCompressionLevel { get; set; }
    public byte ThreadsCount { get; set; }
}

public class LodConfig
{
    public float Quality { get; set; }
    public bool SaveVertexColor { get; set; }
    public bool SaveUv { get; set; }
}