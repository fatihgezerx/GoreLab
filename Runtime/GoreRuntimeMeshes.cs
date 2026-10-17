using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoreLab
{
    /// <summary>Reads a <c>Mesh</c> into plain arrays and builds a <c>Mesh</c> from a part, on the main thread (the splitting between the two runs on another thread).</summary>
    public static class GoreRuntimeMeshes
    {
        /// <summary>Copies the data of a readable mesh. The caller has checked <c>isReadable</c>.</summary>
        public static GoreMeshData Read(Mesh mesh)
        {
            var data = new GoreMeshData
            {
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Colors = mesh.colors32,
                Weights = mesh.boneWeights,
                BindPoses = mesh.bindposes,
                Bounds = mesh.bounds,
                Triangles = new int[mesh.subMeshCount][]
            };

            var tangents = mesh.tangents;
            data.Tangents = tangents.Length == data.Vertices.Length ? tangents : Array.Empty<Vector4>();

            var list = new List<Vector4>(data.Vertices.Length);
            for (var channel = 0; channel < 4; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!mesh.HasVertexAttribute(attribute))
                {
                    continue;
                }

                data.UvDimension[channel] = mesh.GetVertexAttributeDimension(attribute);
                list.Clear();
                mesh.GetUVs(channel, list);
                data.Uvs[channel] = list.ToArray();
            }

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                data.Triangles[s] = mesh.GetSubMesh(s).topology == MeshTopology.Triangles ? mesh.GetTriangles(s) : Array.Empty<int>();
            }

            return data;
        }

        /// <summary>The mesh of one part, with the bind poses and bounds of the mesh it was cut from.</summary>
        public static Mesh Build(GorePartArrays part, GoreMeshData source, string name)
        {
            var count = part.Vertices.Length;
            var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = part.Vertices;
            if (part.Normals.Length == count)
            {
                mesh.normals = part.Normals;
            }

            if (part.Tangents.Length == count)
            {
                mesh.tangents = part.Tangents;
            }

            if (part.Colors.Length == count)
            {
                mesh.colors32 = part.Colors;
            }

            for (var channel = 0; channel < 4; channel++)
            {
                var uvs = part.Uvs[channel];
                if (uvs != null && uvs.Length == count)
                {
                    SetUvs(mesh, channel, uvs, part.UvDimension[channel]);
                }
            }

            mesh.boneWeights = part.Weights;
            mesh.bindposes = source.BindPoses;
            mesh.subMeshCount = part.SubmeshTriangles.Length;
            for (var s = 0; s < part.SubmeshTriangles.Length; s++)
            {
                mesh.SetTriangles(part.SubmeshTriangles[s], s, false);
            }

            mesh.bounds = source.Bounds;
            return mesh;
        }

        /// <summary>The mesh of one piece of a static mesh: no bones, bounds from its own vertices.</summary>
        public static Mesh BuildStatic(GorePartArrays part, string name)
        {
            var count = part.Vertices.Length;
            var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = part.Vertices;
            if (part.Normals.Length == count)
            {
                mesh.normals = part.Normals;
            }

            if (part.Tangents.Length == count)
            {
                mesh.tangents = part.Tangents;
            }

            if (part.Colors.Length == count)
            {
                mesh.colors32 = part.Colors;
            }

            for (var channel = 0; channel < 4; channel++)
            {
                var uvs = part.Uvs[channel];
                if (uvs != null && uvs.Length == count)
                {
                    SetUvs(mesh, channel, uvs, part.UvDimension[channel]);
                }
            }

            mesh.subMeshCount = part.SubmeshTriangles.Length;
            for (var s = 0; s < part.SubmeshTriangles.Length; s++)
            {
                mesh.SetTriangles(part.SubmeshTriangles[s], s, false);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        private static void SetUvs(Mesh mesh, int channel, Vector4[] uvs, int dimension)
        {
            if (dimension <= 2)
            {
                var list = new List<Vector2>(uvs.Length);
                for (var i = 0; i < uvs.Length; i++)
                {
                    list.Add(uvs[i]);
                }

                mesh.SetUVs(channel, list);
            }
            else if (dimension == 3)
            {
                var list = new List<Vector3>(uvs.Length);
                for (var i = 0; i < uvs.Length; i++)
                {
                    list.Add(uvs[i]);
                }

                mesh.SetUVs(channel, list);
            }
            else
            {
                mesh.SetUVs(channel, new List<Vector4>(uvs));
            }
        }
    }
}
