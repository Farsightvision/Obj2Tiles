using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SilentWave.Obj2Gltf.WaveFront;
using System.IO;
using Newtonsoft.Json;
using SilentWave.Obj2Gltf.Gltf;
using Obj2Tiles.Native;

namespace SilentWave.Obj2Gltf
{
    /// <summary>obj2gltf converter (HLOD variant; legacy pipeline uses <see cref="Converter"/>).</summary>
    public class Converter_Hlod
    {
        public static Converter_Hlod MakeDefault() => new(new ObjParser(), new MtlParser());

        private readonly ObjParser _objParser;
        private readonly IMtlParser _mtlParser;
        public Converter_Hlod(ObjParser objParser, IMtlParser mtlParser)
        {
            _objParser = objParser ?? throw new ArgumentNullException(nameof(objParser));
            _mtlParser = mtlParser ?? throw new ArgumentNullException(nameof(mtlParser));
        }

        public void Convert(string objPath, string gltfPath, GltfConverterOptions_Hlod options = null)
        {
            if (string.IsNullOrWhiteSpace(objPath))
                throw new ArgumentNullException(nameof(objPath));

            options ??= new GltfConverterOptions_Hlod();

            var objModel = _objParser.Parse(objPath, options.RemoveDegenerateFaces, options.ObjEncoding);
            var objFolder = Path.GetDirectoryName(objPath);


            if (!string.IsNullOrEmpty(objModel.MatFilename))
            {
                var matFile = Path.Combine(objFolder, objModel.MatFilename);
                var mats = _mtlParser.ParseAsync(matFile).Result;
                objModel.Materials.AddRange(mats);
            }
            Convert(objModel, gltfPath, options);
            if (options.DeleteOriginals)
            {
                if (!string.IsNullOrEmpty(objModel.MatFilename))
                {
                    var matFile = Path.Combine(objFolder, objModel.MatFilename);
                    File.Delete(matFile);
                }
                File.Delete(objPath);
            }
        }

        private void Convert(ObjModel objModel, string outputFile, GltfConverterOptions_Hlod options = null)
        {
            if (objModel == null) throw new ArgumentNullException(nameof(objModel));
            options ??= new GltfConverterOptions_Hlod();

            if (options.EncodeMeshoptCompression)
            {
                throw new NotImplementedException(
                    "EXT_meshopt_compression encoding is not implemented. " +
                    "Set ApplyMeshoptOptimization=true (without EncodeMeshoptCompression) " +
                    "to get the optimize chain only.");
            }

            var u32IndicesEnabled = objModel.RequiresUint32Indices();

            var gltfModel = new GltfModel();
            using (var bufferState = new BufferState(gltfModel, outputFile, u32IndicesEnabled))
            {
                gltfModel.Scenes.Add(new Scene());
                var materials = objModel.Materials.Select(x => ConvertMaterial(x, t => GetTextureIndex(gltfModel, t)));
                gltfModel.Materials.AddRange(materials);

                var meshes = objModel.Geometries.ToArray();
                var meshesLength = meshes.Length;
                for (var i = 0; i < meshesLength; i++)
                {
                    var mesh = meshes[i];
                    if (!mesh.Faces.Any()) continue;
                    var meshIndex = AddMesh(gltfModel, objModel, bufferState, mesh, options);
                    AddNode(gltfModel, mesh.Id, meshIndex, null);
                }
            }

            if (gltfModel.Images.Count > 0)
            {
                gltfModel.Samplers.Add(new TextureSampler
                {
                    MagFilter = MagnificationFilterKind.Linear,
                    // LeafNoMips: Linear minFilter is sharper but aliases under motion.
                    MinFilter = options.LeafNoMips
                        ? MinificationFilterKind.Linear
                        : MinificationFilterKind.NearestMipmapLinear,
                    WrapS = TextureWrappingMode.Repeat,
                    WrapT = TextureWrappingMode.Repeat
                });
            }

            WriteFile(gltfModel, outputFile);
        }

        private void WriteFile(GltfModel gltfModel, string outputFile)
        {
            if (gltfModel == null) throw new ArgumentNullException();
            using var file = File.CreateText(outputFile);
            ToJson(gltfModel, file);
        }

        private static void ToJson(object model, StreamWriter sw)
        {
            var serializer = new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                ContractResolver = new CustomContractResolver()
            };
            serializer.Serialize(sw, model);
        }

        private bool CheckWindingCorrect(SVec3 a, SVec3 b, SVec3 c, SVec3 normal)
        {
            var ba = new SVec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var ca = new SVec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var cross = SVec3.Cross(ba, ca);

            return SVec3.Dot(normal, cross) >= 0;
        }


        #region Materials

        public static double Luminance(FactorColor color)
        {
            return color.Red * 0.2125 + color.Green * 0.7154 + color.Blue * 0.0721;
        }

        private int AddTexture(GltfModel gltfModel, string textureFilename)
        {
            var image = new Image
            {
                Name = textureFilename,
                Uri = textureFilename
            };
            var imageIndex = gltfModel.AddImage(image);

            var textureIndex = gltfModel.Textures.Count;
            var t = new Gltf.Texture
            {
                Name = textureFilename,
                Source = imageIndex,
                Sampler = 0
            };
            gltfModel.Textures.Add(t);
            return textureIndex;
        }



        private Gltf.Material GetDefault(string name = "default", AlphaMode mode = AlphaMode.OPAQUE)
        {
            return new Gltf.Material
            {
                AlphaMode = mode,
                Name = name,
                PbrMetallicRoughness = new PbrMetallicRoughness
                {
                    BaseColorFactor = new double[] { 0.5, 0.5, 0.5, 1 },
                    MetallicFactor = 1.0,
                    RoughnessFactor = 0.0
                }
            };
        }

        private static double Clamp(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }
        private static double ConvertTraditional2MetallicRoughness(WaveFront.Material mat)
        {
            var roughnessFactor = 1.0 - mat.SpecularExponent / 1000.0;
            roughnessFactor = Clamp(roughnessFactor, 0.0, 1.0);

            if (mat.Specular == null || mat.Specular.Color == null)
            {
                mat.Specular = new Reflectivity(new FactorColor());
                return roughnessFactor;
            }
            var specularIntensity = Luminance(mat.Specular.Color);

            // Low specular intensity forces a rough material even when shininess is high.
            if (specularIntensity < 0.1)
            {
                roughnessFactor *= (1.0 - specularIntensity);
            }

            var metallicFactor = 0.0;
            mat.Specular = new Reflectivity(new FactorColor(metallicFactor));
            return roughnessFactor;
        }

        private int AddMaterial(GltfModel gltfModel, Gltf.Material material)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            var matIndex = gltfModel.Materials.Count;
            gltfModel.Materials.Add(material);
            return matIndex;
        }

        int GetTextureIndex(GltfModel gltfModel, string path)
        {
            for (var i = 0; i < gltfModel.Textures.Count; i++)
            {
                if (path == gltfModel.Textures[i].Name)
                {
                    return i;
                }
            }
            return AddTexture(gltfModel, path);
        }

        public static Gltf.Material ConvertMaterial(WaveFront.Material mat, GetOrAddTexture getOrAddTextureFunction)
        {
            var roughnessFactor = ConvertTraditional2MetallicRoughness(mat);

            var gMat = new Gltf.Material
            {
                Name = mat.Name,
                AlphaMode = AlphaMode.OPAQUE
            };

            var alpha = mat.GetAlpha();
            var metallicFactor = 0.0;
            if (mat.Specular != null && mat.Specular.Color != null)
            {
                metallicFactor = mat.Specular.Color.Red;
            }
            gMat.PbrMetallicRoughness = new PbrMetallicRoughness
            {
                RoughnessFactor = roughnessFactor,
                MetallicFactor = metallicFactor
            };
            if (mat.Diffuse != null)
            {
                gMat.PbrMetallicRoughness.BaseColorFactor = mat.Diffuse.Color.ToArray(alpha);
            }
            else if (mat.Ambient != null)
            {
                gMat.PbrMetallicRoughness.BaseColorFactor = mat.Ambient.Color.ToArray(alpha);
            }
            else
            {
                gMat.PbrMetallicRoughness.BaseColorFactor = new double[] { 0.7, 0.7, 0.7, alpha };
            }


            var hasTexture = !string.IsNullOrEmpty(mat.DiffuseTextureFile);
            if (hasTexture)
            {
                var index = getOrAddTextureFunction(mat.DiffuseTextureFile);
                gMat.PbrMetallicRoughness.BaseColorTexture = new TextureReferenceInfo
                {
                    Index = index
                };
            }


            var hasNormalTexture = !string.IsNullOrEmpty(mat.NormalTextureFile);
            if (hasNormalTexture)
            {
                var index = getOrAddTextureFunction(mat.NormalTextureFile);
                gMat.normalTexture = new TextureReferenceInfo
                {
                    Index = index
                };
            }


            if (mat.Emissive != null && mat.Emissive.Color != null)
            {
                gMat.EmissiveFactor = mat.Emissive.Color.ToArray();
            }

            if (alpha < 1.0)
            {
                gMat.AlphaMode = AlphaMode.BLEND;
                gMat.DoubleSided = true;
            }

            return gMat;
        }

        private int GetMaterialIndex(GltfModel gltfModel, string matName)
        {
            for (var i = 0; i < gltfModel.Materials.Count; i++)
            {
                if (gltfModel.Materials[i].Name == matName)
                {
                    return i;
                }
            }
            return -1;
        }

        #endregion Materials

        #region Meshes

        private int AddMesh(GltfModel gltfModel, ObjModel objModel, BufferState buffer, Geometry mesh,
            GltfConverterOptions_Hlod options)
        {
            var ps = AddVertexAttributes(gltfModel, objModel, buffer, mesh, options);

            var m = new Mesh
            {
                Name = mesh.Id,
                Primitives = ps
            };
            var meshIndex = gltfModel.Meshes.Count;
            gltfModel.Meshes.Add(m);
            return meshIndex;
        }

        private List<Primitive> AddVertexAttributes(GltfModel gltfModel,
            ObjModel objModel,
            BufferState bufferState,
            Geometry mesh,
            GltfConverterOptions_Hlod options)
        {
            var facesGroup = mesh.Faces.GroupBy(c => c.MatName);
            var faces = new List<Face>();
            foreach (var fg in facesGroup)
            {
                var matName = fg.Key;
                var f = new Face(matName);
                
                foreach (var ff in fg)
                    f.Triangles.AddRange(ff.Triangles);
                
                if (f.Triangles.Count > 0)
                    faces.Add(f);
                
            }

            var name0 = mesh.Id;
            var hasColors = objModel.Colors.Count > 0;
            var ps = new List<Primitive>(faces.Count * 2);
            var index = 0;
            
            foreach (var f in faces)
            {
                var faceName = name0;
                if (index > 0)
                {
                    faceName = $"{name0}_{index}";
                }

                var hasUvs = f.Triangles.Any(d => d.V1.T > 0);
                var hasNormals = f.Triangles.Any(d => d.V1.N > 0);
                var materialIndex = GetMaterialIndexOrDefault(gltfModel, objModel, f.MatName);
                var material = materialIndex < objModel.Materials.Count ? objModel.Materials[materialIndex] : null;
                var materialHasTexture = hasUvs && material?.DiffuseTextureFile != null;

                var faceVertexCache = new Dictionary<string, int>();
                var faceVertexCount = 0;

                var atts = new Dictionary<string, int>();
                var indicesAccessorIndex = bufferState.MakeIndicesAccessor(faceName + "_indices");
                
                var accessorIndex = bufferState.MakePositionAccessor(faceName + "_positions");
                atts.Add("POSITION", accessorIndex);

                if (hasColors)
                {
                    var colorsAccessorIndex = bufferState.MakeColorsAccessors(faceName + "_colors");
                    atts.Add("COLOR_0", colorsAccessorIndex);
                }

                if (hasNormals)
                {
                    var normalsAccessorIndex = bufferState.MakeNormalAccessors(faceName + "_normals");
                    atts.Add("NORMAL", normalsAccessorIndex);
                }

                if (materialHasTexture)
                {
                    if (hasUvs)
                    {
                        var uvAccessorIndex = bufferState.MakeUvAccessor(faceName + "_texcoords");
                        atts.Add("TEXCOORD_0", uvAccessorIndex);
                    }
                    else
                    {
                        var gMat = gltfModel.Materials[materialIndex];
                        if (gMat.PbrMetallicRoughness.BaseColorTexture != null)
                        {
                            gMat.PbrMetallicRoughness.BaseColorTexture = null;
                        }
                    }
                }

                var primPositions = new List<SVec3>();
                var primColors = hasColors ? new List<SVec3>() : null;
                var primNormals = hasNormals ? new List<SVec3>() : null;
                var primUvs = (materialHasTexture && hasUvs) ? new List<SVec2>() : null;
                var iList = new List<int>(f.Triangles.Count * 3 * 2);
                foreach (var triangle in f.Triangles)
                {
                    var v1Index = triangle.V1.V - 1;
                    var v2Index = triangle.V2.V - 1;
                    var v3Index = triangle.V3.V - 1;
                    var v1 = objModel.Vertices[v1Index];
                    var v2 = objModel.Vertices[v2Index];
                    var v3 = objModel.Vertices[v3Index];

                    var c1 = new SVec3();
                    var c2 = new SVec3();
                    var c3 = new SVec3();

                    if (hasColors)
                    {
                        c1 = objModel.Colors[v1Index];
                        c2 = objModel.Colors[v2Index];
                        c3 = objModel.Colors[v3Index];
                    }

                    var n1 = new SVec3();
                    var n2 = new SVec3();
                    var n3 = new SVec3();

                    if (triangle.V1.N > 0)
                    {
                        var n1Index = triangle.V1.N - 1;
                        var n2Index = triangle.V2.N - 1;
                        var n3Index = triangle.V3.N - 1;
                        n1 = objModel.Normals[n1Index];
                        n2 = objModel.Normals[n2Index];
                        n3 = objModel.Normals[n3Index];
                    }

                    var t1 = new SVec2();
                    var t2 = new SVec2();
                    var t3 = new SVec2();

                    if (materialHasTexture)
                    {
                        if (triangle.V1.T > 0)
                        {
                            var t1Index = triangle.V1.T - 1;
                            var t2Index = triangle.V2.T - 1;
                            var t3Index = triangle.V3.T - 1;
                            t1 = objModel.Uvs[t1Index];
                            t2 = objModel.Uvs[t2Index];
                            t3 = objModel.Uvs[t3Index];
                        }
                    }

                    var v1Str = triangle.V1.ToString();
                    if (!faceVertexCache.ContainsKey(v1Str))
                    {
                        faceVertexCache.Add(v1Str, faceVertexCount++);
                        primPositions.Add(v1);

                        if (hasColors)
                        {
                            primColors.Add(c1);
                        }
                        if (hasNormals)
                        {
                            primNormals.Add(n1);
                        }
                        if (primUvs != null)
                        {
                            primUvs.Add(triangle.V1.T > 0 ? new SVec2(t1.U, 1 - t1.V) : new SVec2(0, 0));
                        }
                    }

                    var v2Str = triangle.V2.ToString();
                    if (!faceVertexCache.ContainsKey(v2Str))
                    {
                        faceVertexCache.Add(v2Str, faceVertexCount++);
                        primPositions.Add(v2);

                        if (hasColors)
                        {
                            primColors.Add(c2);
                        }
                        if (hasNormals)
                        {
                            primNormals.Add(n2);
                        }
                        if (primUvs != null)
                        {
                            primUvs.Add(triangle.V2.T > 0 ? new SVec2(t2.U, 1 - t2.V) : new SVec2(0, 0));
                        }
                    }

                    var v3Str = triangle.V3.ToString();
                    if (!faceVertexCache.ContainsKey(v3Str))
                    {
                        faceVertexCache.Add(v3Str, faceVertexCount++);
                        primPositions.Add(v3);

                        if (hasColors)
                        {
                            primColors.Add(c3);
                        }
                        if (hasNormals)
                        {
                            primNormals.Add(n3);
                        }
                        if (primUvs != null)
                        {
                            primUvs.Add(triangle.V3.T > 0 ? new SVec2(t3.U, 1 - t3.V) : new SVec2(0, 0));
                        }
                    }

                    var correctWinding = CheckWindingCorrect(v1, v2, v3, n1);
                    if (correctWinding)
                    {
                        iList.AddRange(new[] {
                            faceVertexCache[v1Str],
                            faceVertexCache[v2Str],
                            faceVertexCache[v3Str]
                        });
                    }
                    else
                    {
                        iList.AddRange(new[] {
                            faceVertexCache[v1Str],
                            faceVertexCache[v3Str],
                            faceVertexCache[v2Str]
                        });
                    }
                }

                int[] vertexRemap = null;
                int optimizedVertexCount = faceVertexCount;
                if (options.ApplyMeshoptOptimization && faceVertexCount > 0 && iList.Count > 0)
                {
                    (iList, vertexRemap, optimizedVertexCount) = ApplyMeshoptChain(
                        iList, primPositions, options.OverdrawThreshold);
                }

                EmitPrimitiveAttributes(
                    bufferState, primPositions, primColors, primNormals, primUvs,
                    vertexRemap, optimizedVertexCount, hasColors, hasNormals);

                foreach (var i in iList)
                {
                    bufferState.AddIndex(i);
                }

                var p = new Primitive
                {
                    Attributes = atts,
                    Indices = indicesAccessorIndex,
                    Material = materialIndex,
                    Mode = MeshMode.Triangles
                };
                ps.Add(p);


                index++;
            }

            return ps;
        }

        private int GetMaterialIndexOrDefault(GltfModel gltfModel, ObjModel objModel, string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) materialName = "default";

            var materialIndex = GetMaterialIndex(gltfModel, materialName);
            if (materialIndex == -1)
            {
                var objMaterial = objModel.Materials.FirstOrDefault(c => c.Name == materialName);
                if (objMaterial == null)
                {
                    materialName = "default";
                    materialIndex = GetMaterialIndex(gltfModel, materialName);
                    if (materialIndex == -1)
                    {
                        var gMat = GetDefault();
                        materialIndex = AddMaterial(gltfModel, gMat);
                    }
                    else
                    {
#if DEBUG
                        Debugger.Break();
#endif
                    }
                }
                else
                {
                    var gMat = ConvertMaterial(objMaterial, t => GetTextureIndex(gltfModel, t));
                    materialIndex = AddMaterial(gltfModel, gMat);
                }
            }

            return materialIndex;
        }

        private int AddNode(GltfModel gltfModel, string name, int? meshIndex, int? parentIndex = null)
        {
            var node = new Node { Name = name, Mesh = meshIndex };
            var nodeIndex = gltfModel.Nodes.Count;
            gltfModel.Nodes.Add(node);

            gltfModel.Scenes[gltfModel.Scene].Nodes.Add(nodeIndex);

            return nodeIndex;
        }

        #endregion Meshes

        #region Meshoptimizer integration

        /// <summary>
        /// Meshoptimizer optimize chain (vertex-cache → overdraw → vertex-fetch reorder).
        /// Returns the rewritten indices, the vertex remap (old index → new, -1 if dropped),
        /// and the new vertex count. Pure permutation; no vertex data is modified.
        /// </summary>
        private static (List<int> indices, int[] vertexRemap, int newVertexCount)
            ApplyMeshoptChain(List<int> iList, List<SVec3> positions, float overdrawThreshold)
        {
            int vertexCount = positions.Count;
            var indicesU = new uint[iList.Count];
            for (var i = 0; i < iList.Count; i++) indicesU[i] = (uint)iList[i];

            var positionsXyz = new float[vertexCount * 3];
            for (var i = 0; i < vertexCount; i++)
            {
                positionsXyz[i * 3 + 0] = positions[i].X;
                positionsXyz[i * 3 + 1] = positions[i].Y;
                positionsXyz[i * 3 + 2] = positions[i].Z;
            }

            var optIndices = new uint[indicesU.Length];
            Meshopt.OptimizeVertexCache(optIndices, indicesU, vertexCount);

            // overdrawThreshold: vertex-cache locality to trade for less overdraw (1.05 = 5%).
            var overdrawIndices = new uint[optIndices.Length];
            Meshopt.OptimizeOverdraw(overdrawIndices, optIndices, positionsXyz, overdrawThreshold);

            // Vertex-fetch may drop vertices; run it over synthetic 4-byte vertex IDs
            // to recover the old→new permutation for the parallel attribute lists.
            var oldIds = new byte[vertexCount * sizeof(uint)];
            for (var i = 0; i < vertexCount; i++)
            {
                var b = BitConverter.GetBytes((uint)i);
                System.Buffer.BlockCopy(b, 0, oldIds, i * sizeof(uint), sizeof(uint));
            }
            var newIds = new byte[oldIds.Length];
            int newVertexCount = Meshopt.OptimizeVertexFetch(
                newIds, overdrawIndices, oldIds, vertexCount, sizeof(uint));

            // newIds[i] holds the old id at new position i; invert to old→new.
            var vertexRemap = new int[vertexCount];
            for (var i = 0; i < vertexCount; i++) vertexRemap[i] = -1;
            for (var newPos = 0; newPos < newVertexCount; newPos++)
            {
                uint oldId = BitConverter.ToUInt32(newIds, newPos * sizeof(uint));
                vertexRemap[oldId] = newPos;
            }

            // Rewrite indices from old vertex positions to new ones.
            var outIndices = new List<int>(overdrawIndices.Length);
            for (var i = 0; i < overdrawIndices.Length; i++)
            {
                outIndices.Add(vertexRemap[(int)overdrawIndices[i]]);
            }

            return (outIndices, vertexRemap, newVertexCount);
        }

        /// <summary>
        /// Emit attributes to the BufferState; collection order when vertexRemap is null,
        /// vertex-fetch order otherwise.
        /// </summary>
        private static void EmitPrimitiveAttributes(
            BufferState bufferState,
            List<SVec3> positions,
            List<SVec3> colors,
            List<SVec3> normals,
            List<SVec2> uvs,
            int[] vertexRemap,
            int optimizedVertexCount,
            bool hasColors,
            bool hasNormals)
        {
            if (vertexRemap is null)
            {
                for (var i = 0; i < positions.Count; i++)
                {
                    bufferState.AddPosition(positions[i]);
                    if (hasColors) bufferState.AddColor(colors[i]);
                    if (hasNormals) bufferState.AddNormal(normals[i]);
                    if (uvs != null) bufferState.AddUv(uvs[i]);
                }
                return;
            }

            // Invert vertexRemap to new→old so attributes emit in new-order.
            var inverse = new int[optimizedVertexCount];
            for (var oldIdx = 0; oldIdx < vertexRemap.Length; oldIdx++)
            {
                int newIdx = vertexRemap[oldIdx];
                if (newIdx >= 0) inverse[newIdx] = oldIdx;
            }
            for (var newIdx = 0; newIdx < optimizedVertexCount; newIdx++)
            {
                int oldIdx = inverse[newIdx];
                bufferState.AddPosition(positions[oldIdx]);
                if (hasColors) bufferState.AddColor(colors[oldIdx]);
                if (hasNormals) bufferState.AddNormal(normals[oldIdx]);
                if (uvs != null) bufferState.AddUv(uvs[oldIdx]);
            }
        }

        #endregion Meshoptimizer integration
    }
}
