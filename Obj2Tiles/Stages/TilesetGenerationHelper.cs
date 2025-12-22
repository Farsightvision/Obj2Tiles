using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages.Model;

namespace Obj2Tiles.Stages;

public static class TilesetGenerationHelper
{
    /// <summary>
    /// Prepares bounds mapper from meshes for tileset generation.
    /// </summary>
    /// <param name="meshes">Meshes grouped by LOD configuration from Split stage</param>
    /// <param name="lodConfigs">LOD configurations in sequential order</param>
    /// <returns>
    /// Array of dictionaries: Dictionary&lt;string, Box3&gt;[]
    /// - Array indexed by int (array[0] = LOD 0, array[1] = LOD 1, etc.)
    /// - Each dictionary maps: mesh name (string) → bounding box (Box3)
    /// Example access: boundsMapper[lodLevel]["meshName"] → Box3
    /// </returns>
    public static Dictionary<string, Box3>[] PrepareBoundsMapper(
        Dictionary<LodConfig, List<IMesh>> meshes,
        LodConfig[] lodConfigs)
    {
        var boundsMapper = new Dictionary<string, Box3>[lodConfigs.Length];
        
        for (var lodIndex = 0; lodIndex < lodConfigs.Length; lodIndex++)
        {
            var lodConfig = lodConfigs[lodIndex];
            var lodMeshes = meshes.TryGetValue(lodConfig, out var list) ? list : new List<IMesh>();
            var dict = new Dictionary<string, Box3>();
            
            for (var i = 0; i < lodMeshes.Count; i++)
            {
                var mesh = lodMeshes[i];
                // Skip unnamed meshes (they can't be referenced in tileset.json)
                if (!string.IsNullOrWhiteSpace(mesh.Name))
                    dict[mesh.Name] = mesh.Bounds;
            }
            
            boundsMapper[lodIndex] = dict;
        }
        
        return boundsMapper;
    }

    public static GpsCoords? CreateGpsCoords(double? latitude, double? longitude, 
        double altitude, double scale, bool yUpToZUp)
    {
        if (!latitude.HasValue || !longitude.HasValue)
            return null;

        return new GpsCoords
        {
            Latitude = latitude.Value,
            Longitude = longitude.Value,
            Altitude = altitude,
            Scale = scale,
            YUpToZUp = yUpToZUp
        };
    }
}

