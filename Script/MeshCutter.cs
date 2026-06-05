using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleFracture
{
    public static class MeshCutter
    {
        private const float EPS = 0.0001f;

        public struct SliceResult
        {
            public Mesh positive;
            public Mesh negative;
        }

        private struct VertexData
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;

            public VertexData(Vector3 position, Vector3 normal, Vector2 uv)
            {
                this.position = position;
                this.normal = normal;
                this.uv = uv;
            }
        }

        private struct Segment
        {
            public Vector3 a;
            public Vector3 b;

            public Segment(Vector3 a, Vector3 b)
            {
                this.a = a;
                this.b = b;
            }
        }

        private struct PointKey : IEquatable<PointKey>
        {
            private readonly int x;
            private readonly int y;
            private readonly int z;

            public PointKey(Vector3 p)
            {
                x = Mathf.RoundToInt(p.x / EPS);
                y = Mathf.RoundToInt(p.y / EPS);
                z = Mathf.RoundToInt(p.z / EPS);
            }

            /// <summary>
            /// 构造一个 PointKey，直接使用已量化的网格坐标。
            /// 用于在 BuildLoopsFromSegments 中检查相邻网格单元。
            /// </summary>
            public PointKey(int quantizedX, int quantizedY, int quantizedZ)
            {
                x = quantizedX;
                y = quantizedY;
                z = quantizedZ;
            }

            public bool Equals(PointKey other)
            {
                return x == other.x && y == other.y && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is PointKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x;
                    hash = hash * 31 + y;
                    hash = hash * 31 + z;
                    return hash;
                }
            }
        }

        private class MeshBuilder
        {
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> triangles = new List<int>();

            private readonly bool useNormals;
            private readonly bool useUVs;

            public MeshBuilder(bool useNormals, bool useUVs)
            {
                this.useNormals = useNormals;
                this.useUVs = useUVs;
            }

            public int AddVertex(VertexData v)
            {
                int index = vertices.Count;
                vertices.Add(v.position);

                if (useNormals)
                    normals.Add(v.normal);

                if (useUVs)
                    uvs.Add(v.uv);

                return index;
            }

            public void AddTriangle(VertexData a, VertexData b, VertexData c)
            {
                int ia = AddVertex(a);
                int ib = AddVertex(b);
                int ic = AddVertex(c);

                triangles.Add(ia);
                triangles.Add(ib);
                triangles.Add(ic);
            }

            public Mesh ToMesh(string name)
            {
                Mesh mesh = new Mesh();
                mesh.name = name;

                if (vertices.Count > 65535)
                    mesh.indexFormat = IndexFormat.UInt32;

                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);

                if (useUVs && uvs.Count == vertices.Count)
                    mesh.SetUVs(0, uvs);

                if (useNormals && normals.Count == vertices.Count)
                {
                    SmoothNormals();
                    mesh.SetNormals(normals);
                }
                else
                {
                    mesh.RecalculateNormals();
                }

                mesh.RecalculateBounds();
                return mesh;
            }

            private void SmoothNormals()
            {
                if (!useNormals || normals.Count != vertices.Count)
                    return;

                const float normalSimilarityThreshold = 0.95f;
                var positionGroups = new Dictionary<PointKey, List<int>>();

                for (int i = 0; i < vertices.Count; i++)
                {
                    var key = new PointKey(vertices[i]);
                    if (!positionGroups.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        positionGroups[key] = list;
                    }
                    list.Add(i);
                }

                foreach (var group in positionGroups.Values)
                {
                    var normalClusters = new List<List<int>>();

                    foreach (int index in group)
                    {
                        Vector3 normal = normals[index];
                        bool added = false;

                        for (int c = 0; c < normalClusters.Count; c++)
                        {
                            int representative = normalClusters[c][0];
                            if (Vector3.Dot(normals[representative], normal) >= normalSimilarityThreshold)
                            {
                                normalClusters[c].Add(index);
                                added = true;
                                break;
                            }
                        }

                        if (!added)
                            normalClusters.Add(new List<int> { index });
                    }

                    foreach (var cluster in normalClusters)
                    {
                        Vector3 average = Vector3.zero;
                        foreach (int index in cluster)
                            average += normals[index];

                        average.Normalize();

                        foreach (int index in cluster)
                            normals[index] = average;
                    }
                }
            }
        }

        public static SliceResult Slice(Mesh sourceMesh, Vector3 planePointLocal, Vector3 planeNormalLocal)
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));

            if (planeNormalLocal.sqrMagnitude < 1e-8f)
                throw new ArgumentException("Plane normal is zero.", nameof(planeNormalLocal));

            planeNormalLocal.Normalize();

            Vector3[] sourceVertices = sourceMesh.vertices;
            Vector3[] sourceNormals = sourceMesh.normals;
            Vector2[] sourceUVs = sourceMesh.uv;

            bool hasNormals = sourceNormals != null && sourceNormals.Length == sourceMesh.vertexCount;
            bool hasUVs = sourceUVs != null && sourceUVs.Length == sourceMesh.vertexCount;

            MeshBuilder positiveBuilder = new MeshBuilder(hasNormals, hasUVs);
            MeshBuilder negativeBuilder = new MeshBuilder(hasNormals, hasUVs);

            List<Segment> capSegments = new List<Segment>();

            float planeD = Vector3.Dot(planeNormalLocal, planePointLocal);

            for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
            {
                int[] tris = sourceMesh.GetTriangles(subMesh);

                for (int i = 0; i < tris.Length; i += 3)
                {
                    int i0 = tris[i];
                    int i1 = tris[i + 1];
                    int i2 = tris[i + 2];

                    VertexData v0 = GetVertexData(sourceVertices, sourceNormals, sourceUVs, hasNormals, hasUVs, i0);
                    VertexData v1 = GetVertexData(sourceVertices, sourceNormals, sourceUVs, hasNormals, hasUVs, i1);
                    VertexData v2 = GetVertexData(sourceVertices, sourceNormals, sourceUVs, hasNormals, hasUVs, i2);

                    float d0 = SignedDistance(v0.position, planeNormalLocal, planeD);
                    float d1 = SignedDistance(v1.position, planeNormalLocal, planeD);
                    float d2 = SignedDistance(v2.position, planeNormalLocal, planeD);

                    bool hasPos = d0 > EPS || d1 > EPS || d2 > EPS;
                    bool hasNeg = d0 < -EPS || d1 < -EPS || d2 < -EPS;

                    if (hasPos && hasNeg)
                    {
                        Segment seg;
                        if (TryGetIntersectionSegment(v0, d0, v1, d1, v2, d2, out seg))
                            capSegments.Add(seg);
                    }

                    List<VertexData> tri = new List<VertexData>(3);
                    tri.Add(v0);
                    tri.Add(v1);
                    tri.Add(v2);

                    List<VertexData> posPoly = ClipPolygon(tri, planeNormalLocal, planeD, true);
                    if (posPoly.Count >= 3)
                        AddPolygon(positiveBuilder, posPoly, planeNormalLocal, hasNormals);

                    List<VertexData> negPoly = ClipPolygon(tri, planeNormalLocal, planeD, false);
                    if (negPoly.Count >= 3)
                        AddPolygon(negativeBuilder, negPoly, -planeNormalLocal, hasNormals);
                }
            }

            List<List<Vector3>> loops = BuildLoopsFromSegments(capSegments);
            for (int i = 0; i < loops.Count; i++)
            {
                List<Vector3> loop = loops[i];
                if (loop.Count < 3)
                    continue;

                AddCap(positiveBuilder, loop, -planeNormalLocal, hasNormals);
                AddCap(negativeBuilder, loop, planeNormalLocal, hasNormals);
            }

            SliceResult result = new SliceResult
            {
                positive = positiveBuilder.vertices.Count > 0 ? positiveBuilder.ToMesh(sourceMesh.name + "_Positive") : null,
                negative = negativeBuilder.vertices.Count > 0 ? negativeBuilder.ToMesh(sourceMesh.name + "_Negative") : null
            };

            return result;
        }

        private static VertexData GetVertexData(
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            bool hasNormals,
            bool hasUVs,
            int index)
        {
            Vector3 p = vertices[index];
            Vector3 n = hasNormals ? normals[index] : Vector3.up;
            Vector2 uv = hasUVs ? uvs[index] : Vector2.zero;
            return new VertexData(p, n, uv);
        }

        private static float SignedDistance(Vector3 point, Vector3 planeNormal, float planeD)
        {
            return Vector3.Dot(planeNormal, point) - planeD;
        }

        private static VertexData Interpolate(VertexData a, VertexData b, float t, bool hasNormals)
        {
            Vector3 pos = Vector3.LerpUnclamped(a.position, b.position, t);
            Vector3 normal = hasNormals ? Vector3.LerpUnclamped(a.normal, b.normal, t).normalized : Vector3.up;
            Vector2 uv = Vector2.LerpUnclamped(a.uv, b.uv, t);
            return new VertexData(pos, normal, uv);
        }

        private static List<VertexData> ClipPolygon(List<VertexData> input, Vector3 planeNormal, float planeD, bool keepPositive)
        {
            List<VertexData> output = new List<VertexData>();
            if (input == null || input.Count == 0)
                return output;

            for (int i = 0; i < input.Count; i++)
            {
                VertexData current = input[i];
                VertexData next = input[(i + 1) % input.Count];

                float dc = SignedDistance(current.position, planeNormal, planeD);
                float dn = SignedDistance(next.position, planeNormal, planeD);

                bool currentInside = keepPositive ? dc >= -EPS : dc <= EPS;
                bool nextInside = keepPositive ? dn >= -EPS : dn <= EPS;

                if (currentInside && nextInside)
                {
                    output.Add(next);
                }
                else if (currentInside && !nextInside)
                {
                    float t = dc / (dc - dn);
                    output.Add(Interpolate(current, next, t, true));
                }
                else if (!currentInside && nextInside)
                {
                    float t = dc / (dc - dn);
                    output.Add(Interpolate(current, next, t, true));
                    output.Add(next);
                }
            }

            return output;
        }

        private static void AddPolygon(MeshBuilder builder, List<VertexData> poly, Vector3 faceNormal, bool hasNormals)
        {
            if (poly == null || poly.Count < 3)
                return;

            VertexData first = MakeVertex(poly[0], faceNormal, hasNormals);

            for (int i = 1; i < poly.Count - 1; i++)
            {
                builder.AddTriangle(
                    first,
                    MakeVertex(poly[i], faceNormal, hasNormals),
                    MakeVertex(poly[i + 1], faceNormal, hasNormals));
            }
        }

        private static VertexData MakeVertex(VertexData v, Vector3 fallbackNormal, bool hasNormals)
        {
            if (!hasNormals)
                return new VertexData(v.position, fallbackNormal, v.uv);

            if (v.normal == Vector3.zero)
                return new VertexData(v.position, fallbackNormal, v.uv);

            return v;
        }

        private static bool TryGetIntersectionSegment(
            VertexData v0, float d0,
            VertexData v1, float d1,
            VertexData v2, float d2,
            out Segment segment)
        {
            List<Vector3> points = new List<Vector3>(2);

            AddEdgeIntersection(v0, d0, v1, d1, points);
            AddEdgeIntersection(v1, d1, v2, d2, points);
            AddEdgeIntersection(v2, d2, v0, d0, points);

            if (points.Count >= 2)
            {
                segment = new Segment(points[0], points[1]);
                return true;
            }

            segment = default(Segment);
            return false;
        }

        private static void AddEdgeIntersection(VertexData a, float da, VertexData b, float db, List<Vector3> points)
        {
            bool aOn = Mathf.Abs(da) <= EPS;
            bool bOn = Mathf.Abs(db) <= EPS;

            if (aOn && bOn)
                return;

            if (aOn)
            {
                AddUnique(points, a.position);
                return;
            }

            if (bOn)
            {
                AddUnique(points, b.position);
                return;
            }

            if ((da > 0f && db < 0f) || (da < 0f && db > 0f))
            {
                float t = da / (da - db);
                Vector3 p = Vector3.LerpUnclamped(a.position, b.position, t);
                AddUnique(points, p);
            }
        }

        private static void AddUnique(List<Vector3> points, Vector3 p)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if ((points[i] - p).sqrMagnitude <= EPS * EPS)
                    return;
            }

            points.Add(p);
        }

        private static List<List<Vector3>> BuildLoopsFromSegments(List<Segment> segments)
        {
            var loops = new List<List<Vector3>>();
            if (segments == null || segments.Count == 0)
                return loops;

            var pointToId = new Dictionary<PointKey, int>();
            var idToPoint = new List<Vector3>();
            var adjacency = new Dictionary<int, List<int>>();

            int GetPointId(Vector3 p)
            {
                var key = new PointKey(p);
                if (pointToId.TryGetValue(key, out int id))
                    return id;

                // 修正量化边界问题：检查相邻网格单元中是否存在距离在 EPS 内的点。
                // 当两个几乎相同的点位于量化网格边界两侧时，直接 PointKey 查询会失败，
                // 导致本应连接的线段在邻接图中断开，最终产生不封闭的网格。
                int qx = Mathf.RoundToInt(p.x / EPS);
                int qy = Mathf.RoundToInt(p.y / EPS);
                int qz = Mathf.RoundToInt(p.z / EPS);
                float epsSq = EPS * EPS;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0)
                                continue;

                            var neighborKey = new PointKey(qx + dx, qy + dy, qz + dz);
                            if (pointToId.TryGetValue(neighborKey, out int neighborId))
                            {
                                // 验证实际距离是否在容差内
                                if ((idToPoint[neighborId] - p).sqrMagnitude <= epsSq)
                                    return neighborId;
                            }
                        }
                    }
                }

                id = idToPoint.Count;
                pointToId[key] = id;
                idToPoint.Add(p);
                adjacency[id] = new List<int>();
                return id;
            }

            void Connect(int a, int b)
            {
                if (a == b) return;
                if (!adjacency[a].Contains(b))
                    adjacency[a].Add(b);
                if (!adjacency[b].Contains(a))
                    adjacency[b].Add(a);
            }

            foreach (var s in segments)
            {
                int a = GetPointId(s.a);
                int b = GetPointId(s.b);
                Connect(a, b);
            }

            long EdgeKey(int a, int b)
            {
                return ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            }

            var usedEdges = new HashSet<long>();

            foreach (var kvp in adjacency)
            {
                int start = kvp.Key;
                if (adjacency[start].Count == 0)
                    continue;

                foreach (int firstNeighbor in adjacency[start])
                {
                    long firstEdge = EdgeKey(start, firstNeighbor);
                    if (usedEdges.Contains(firstEdge))
                        continue;

                    var loopIndices = new List<int>();
                    int current = start;
                    int previous = -1;
                    int safety = 0;

                    while (safety++ < 10000)
                    {
                        loopIndices.Add(current);

                        int next = -1;
                        var neighbors = adjacency[current];

                        // 当顶点度数为 2 时直接选择非前驱的邻居；
                        // 当度数 != 2 时（多条交线汇聚于同一点），
                        // 选择使环保持最直方向的边，避免追踪出错误的环。
                        if (neighbors.Count == 2)
                        {
                            foreach (int neighbor in neighbors)
                            {
                                if (neighbor == previous)
                                    continue;
                                long edge = EdgeKey(current, neighbor);
                                if (usedEdges.Contains(edge))
                                    continue;
                                next = neighbor;
                                break;
                            }
                        }
                        else
                        {
                            // 度数 != 2：选择最直的方向
                            float bestAngle = float.MaxValue;
                            Vector3 prevDir = previous >= 0
                                ? (idToPoint[current] - idToPoint[previous]).normalized
                                : Vector3.zero;

                            foreach (int neighbor in neighbors)
                            {
                                if (neighbor == previous)
                                    continue;
                                long edge = EdgeKey(current, neighbor);
                                if (usedEdges.Contains(edge))
                                    continue;

                                if (previous < 0)
                                {
                                    // 起始顶点：选择第一个可用邻居（与 firstNeighbor 一致）
                                    next = neighbor;
                                    break;
                                }

                                Vector3 candDir = (idToPoint[neighbor] - idToPoint[current]).normalized;
                                float dot = -Vector3.Dot(prevDir, candDir);
                                float angle = Mathf.Abs(dot + 1f);
                                if (angle < bestAngle)
                                {
                                    bestAngle = angle;
                                    next = neighbor;
                                }
                            }
                        }

                        if (next == -1)
                            break;

                        usedEdges.Add(EdgeKey(current, next));
                        previous = current;
                        current = next;

                        if (current == start)
                        {
                            loopIndices.Add(current);
                            break;
                        }
                    }

                    if (loopIndices.Count >= 4 && loopIndices[0] == loopIndices[loopIndices.Count - 1])
                    {
                        loopIndices.RemoveAt(loopIndices.Count - 1);
                        loops.Add(loopIndices.ConvertAll(i => idToPoint[i]));
                    }
                }
            }

            return loops;
        }

        private static List<Vector3> DeduplicatePoints(List<Vector3> points)
        {
            var result = new List<Vector3>();
            var seen = new HashSet<PointKey>();

            for (int i = 0; i < points.Count; i++)
            {
                var key = new PointKey(points[i]);
                if (seen.Add(key))
                    result.Add(points[i]);
            }

            return result;
        }

        private static void AddCap(
            MeshBuilder builder,
            List<Vector3> loop,
            Vector3 capNormal,
            bool hasNormals)
        {
            if (loop == null || loop.Count < 3)
                return;

            // 第一步：完整去重（保留首次出现顺序），移除可能因浮点误差或
            // 非流形交线追踪产生的非连续重复点。
            List<Vector3> uniquePoints = DeduplicatePoints(loop);
            if (uniquePoints.Count < 3)
                return;

            // 第二步：去除连续重复点（闭合环的首尾重复）
            var cleanPoints = new List<Vector3>(uniquePoints.Count);
            for (int i = 0; i < uniquePoints.Count; i++)
            {
                Vector3 p = uniquePoints[i];
                if (cleanPoints.Count > 0
                    && (cleanPoints[cleanPoints.Count - 1] - p).sqrMagnitude <= EPS * EPS)
                    continue;
                cleanPoints.Add(p);
            }
            if (cleanPoints.Count >= 3
                && (cleanPoints[0] - cleanPoints[cleanPoints.Count - 1]).sqrMagnitude <= EPS * EPS)
            {
                cleanPoints.RemoveAt(cleanPoints.Count - 1);
            }

            if (cleanPoints.Count < 3)
                return;

            BuildPlaneBasis(capNormal, out Vector3 tangent, out Vector3 bitangent);

            // 投影到 2D 平面
            List<Vector2> projected = new List<Vector2>(cleanPoints.Count);
            for (int i = 0; i < cleanPoints.Count; i++)
                projected.Add(Project(cleanPoints[i], tangent, bitangent));

            // 检查 2D 多边形方向，确保为逆时针(CCW)
            if (SignedArea(projected) < 0f)
            {
                cleanPoints.Reverse();
                projected.Reverse();
            }

            bool IsRightOriented(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                return Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f;
            }

            // Ear clipping 三角化
            var indices = TriangulatePolygon(projected);
            if (indices.Count < 3)
            {
                // 回退到扇形三角化
                for (int i = 1; i < cleanPoints.Count - 1; i++)
                {
                    int ia = 0;
                    int ib = i;
                    int ic = i + 1;

                    if (!IsRightOriented(cleanPoints[ia], cleanPoints[ib], cleanPoints[ic], capNormal))
                    {
                        int temp = ib;
                        ib = ic;
                        ic = temp;
                    }

                    builder.AddTriangle(
                        new VertexData(cleanPoints[ia], capNormal, projected[ia]),
                        new VertexData(cleanPoints[ib], capNormal, projected[ib]),
                        new VertexData(cleanPoints[ic], capNormal, projected[ic]));
                }
                return;
            }

            for (int i = 0; i < indices.Count; i += 3)
            {
                int ia = indices[i];
                int ib = indices[i + 1];
                int ic = indices[i + 2];

                if (!IsRightOriented(cleanPoints[ia], cleanPoints[ib], cleanPoints[ic], capNormal))
                {
                    int temp = ib;
                    ib = ic;
                    ic = temp;
                }

                builder.AddTriangle(
                    new VertexData(cleanPoints[ia], capNormal, projected[ia]),
                    new VertexData(cleanPoints[ib], capNormal, projected[ib]),
                    new VertexData(cleanPoints[ic], capNormal, projected[ic]));
            }
        }

        private static Vector2 Project(Vector3 p, Vector3 tangent, Vector3 bitangent)
        {
            return new Vector2(Vector3.Dot(p, tangent), Vector3.Dot(p, bitangent));
        }

        private static void BuildPlaneBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            Vector3 helper = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
            tangent = Vector3.Cross(helper, normal).normalized;
            bitangent = Vector3.Cross(normal, tangent).normalized;
        }

        private static float SignedArea(List<Vector2> poly)
        {
            float area = 0f;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i];
                Vector2 b = poly[(i + 1) % poly.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area * 0.5f;
        }

        private static List<int> TriangulatePolygon(List<Vector2> poly)
        {
            List<int> result = new List<int>();
            if (poly == null || poly.Count < 3)
                return result;

            List<int> indices = new List<int>();
            for (int i = 0; i < poly.Count; i++)
                indices.Add(i);

            if (SignedArea(poly) < 0f)
            {
                poly.Reverse();
                indices.Reverse();
            }

            int safety = 0;
            while (indices.Count > 3 && safety++ < 10000)
            {
                bool earFound = false;

                for (int i = 0; i < indices.Count; i++)
                {
                    int prev = indices[(i - 1 + indices.Count) % indices.Count];
                    int curr = indices[i];
                    int next = indices[(i + 1) % indices.Count];

                    Vector2 a = poly[prev];
                    Vector2 b = poly[curr];
                    Vector2 c = poly[next];

                    if (!IsConvex(a, b, c))
                        continue;

                    bool containsPoint = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        int p = indices[j];
                        if (p == prev || p == curr || p == next)
                            continue;

                        if (PointInTriangle(poly[p], a, b, c))
                        {
                            containsPoint = true;
                            break;
                        }
                    }

                    if (containsPoint)
                        continue;

                    result.Add(prev);
                    result.Add(curr);
                    result.Add(next);
                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound)
                {
                    for (int i = 1; i < indices.Count - 1; i++)
                    {
                        result.Add(indices[0]);
                        result.Add(indices[i]);
                        result.Add(indices[i + 1]);
                    }
                    return result;
                }
            }

            if (indices.Count == 3)
            {
                result.Add(indices[0]);
                result.Add(indices[1]);
                result.Add(indices[2]);
            }

            return result;
        }

        private static bool IsConvex(Vector2 a, Vector2 b, Vector2 c)
        {
            return Cross(b - a, c - b) > 0f;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float ab = Cross(b - a, p - a);
            float bc = Cross(c - b, p - b);
            float ca = Cross(a - c, p - c);

            bool hasNeg = (ab < 0f) || (bc < 0f) || (ca < 0f);
            bool hasPos = (ab > 0f) || (bc > 0f) || (ca > 0f);
            return !(hasNeg && hasPos);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly int a;
            private readonly int b;

            public EdgeKey(int i0, int i1)
            {
                if (i0 < i1)
                {
                    a = i0;
                    b = i1;
                }
                else
                {
                    a = i1;
                    b = i0;
                }
            }

            public int GetA()
            {
                return a;
            }

            public int GetB()
            {
                return b;
            }

            public bool Equals(EdgeKey other)
            {
                return a == other.a && b == other.b;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 29;
                    hash = hash * 31 + a;
                    hash = hash * 31 + b;
                    return hash;
                }
            }
        }
    }
}