using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SimpleFracture;

public class MeshFractureWindow : EditorWindow
{
    private enum CutStyle
    {
        Random,
        Horizontal,
        Vertical,
        Slanted
    }

    private enum FractureMethod
    {
        IterativeSlice,
        CellFracture
    }

    [SerializeField] private GameObject target;
    [SerializeField] private FractureMethod fractureMethod = FractureMethod.IterativeSlice;
    [SerializeField] private int targetPieces = 8;
    [SerializeField] private int maxAttempts = 64;
    [SerializeField] private bool destroyOriginal = true;
    [SerializeField] private bool useRandomSeed = true;
    [SerializeField] private int seed = 12345;
    [SerializeField] private CutStyle cutStyle = CutStyle.Random;
    [SerializeField] private bool addRigidbodies = true;
    [SerializeField] private bool addMeshColliders = true;
    [SerializeField] private float fragmentScale = 1f;

    // CellFracture 参数
    [SerializeField] private int cellCount = 8;
    [SerializeField] private float cellNoise = 0f;
    [SerializeField] private int cellPointLimit = 100;
    [SerializeField] private float cellGap = 0.001f;

    private System.Random rng;

    private class Chunk
    {
        public GameObject go;
        public MeshFilter mf;
        public MeshRenderer mr;

        public Bounds Bounds => mr != null ? mr.bounds : new Bounds(go.transform.position, Vector3.one);
    }

    [MenuItem("Tools/Mesh Fracture")]
    public static void Open()
    {
        GetWindow<MeshFractureWindow>("Mesh Fracture");
    }

    private void OnEnable()
    {
        if (Selection.activeGameObject != null)
            target = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
        target = (GameObject)EditorGUILayout.ObjectField("GameObject", target, typeof(GameObject), true);

        EditorGUILayout.Space(6);

        fractureMethod = (FractureMethod)EditorGUILayout.EnumPopup("Fracture Method", fractureMethod);

        EditorGUILayout.Space(4);

        if (fractureMethod == FractureMethod.IterativeSlice)
        {
            DrawIterativeSliceUI();
        }
        else
        {
            DrawCellFractureUI();
        }

        EditorGUILayout.Space(6);

        destroyOriginal = EditorGUILayout.Toggle("Destroy Original", destroyOriginal);
        addRigidbodies = EditorGUILayout.Toggle("Add Rigidbody", addRigidbodies);
        addMeshColliders = EditorGUILayout.Toggle("Add MeshCollider", addMeshColliders);
        fragmentScale = EditorGUILayout.Slider("Fragment Scale", fragmentScale, 0.1f, 5f);

        EditorGUILayout.Space(10);

        GUI.enabled = target != null;
        string buttonLabel = fractureMethod == FractureMethod.IterativeSlice ? "Fracture To Count" : "Cell Fracture";
        if (GUILayout.Button(buttonLabel))
        {
            if (fractureMethod == FractureMethod.IterativeSlice)
                FractureToCount();
            else
                FractureCell();
        }
        GUI.enabled = true;

        EditorGUILayout.Space(8);
    }

    private void DrawIterativeSliceUI()
    {
        targetPieces = EditorGUILayout.IntField("Target Pieces", targetPieces);
        maxAttempts = EditorGUILayout.IntField("Max Attempts", maxAttempts);
        cutStyle = (CutStyle)EditorGUILayout.EnumPopup("Cut Style", cutStyle);
        useRandomSeed = EditorGUILayout.Toggle("Use Random Seed", useRandomSeed);
        if (!useRandomSeed)
            seed = EditorGUILayout.IntField("Seed", seed);
    }

    private void DrawCellFractureUI()
    {
        cellCount = EditorGUILayout.IntSlider("Cell Count", cellCount, 2, 50);
        cellNoise = EditorGUILayout.Slider("Cell Noise", cellNoise, 0f, 1f);
        cellPointLimit = EditorGUILayout.IntSlider("Point Limit", cellPointLimit, 0, 5000);
        cellGap = EditorGUILayout.Slider("Cell Gap", cellGap, 0f, 0.01f);
        useRandomSeed = EditorGUILayout.Toggle("Use Random Seed", useRandomSeed);
        if (!useRandomSeed)
            seed = EditorGUILayout.IntField("Seed", seed);
    }

    private void FractureToCount()
    {
        if (target == null)
            return;

        if (targetPieces < 2)
            targetPieces = 2;

        var mf = target.GetComponent<MeshFilter>();
        var mr = target.GetComponent<MeshRenderer>();

        if (mf == null || mr == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("Target must have MeshFilter, MeshRenderer, and a valid mesh.");
            return;
        }

        rng = useRandomSeed ? new System.Random() : new System.Random(seed);

        GameObject parent = new GameObject(target.name + "_Fractured");
        Undo.RegisterCreatedObjectUndo(parent, "Create Fracture Parent");
        parent.transform.SetParent(target.transform.parent, false);
        parent.transform.position = target.transform.position;
        parent.transform.rotation = target.transform.rotation;
        parent.transform.localScale = target.transform.localScale;

        // 添加 MeshScale 组件，用于滑动统一控制所有子碎片缩放
        MeshScale meshScale = parent.AddComponent<MeshScale>();
        meshScale.缩放 = 1f;

        var chunks = new List<Chunk>();
        if (destroyOriginal)
        {
            chunks.Add(CreateChunkFromExisting(target, mf.sharedMesh, mr, "_0", parent.transform));
        }
        else
        {
            chunks.Add(CreateChunkFromMesh(target, mf.sharedMesh, mr, "_0", parent.transform));
        }

        int attempts = 0;

        while (chunks.Count < targetPieces && attempts < maxAttempts)
        {
            attempts++;

            int index = GetLargestChunkIndex(chunks);
            if (index < 0)
                break;

            Chunk chunk = chunks[index];
            if (chunk == null || chunk.go == null || chunk.mf == null || chunk.mf.sharedMesh == null)
            {
                chunks.RemoveAt(index);
                continue;
            }

            Bounds bounds = chunk.Bounds;
            Vector3 planePointWorld = bounds.center;
            Vector3 planeNormalWorld = GetPlaneNormalWorld(rng, cutStyle);

            Vector3 planePointLocal = chunk.go.transform.InverseTransformPoint(planePointWorld);
            Vector3 planeNormalLocal = chunk.go.transform.InverseTransformDirection(planeNormalWorld).normalized;

            if (planeNormalLocal.sqrMagnitude < 1e-8f)
                continue;

            MeshCutter.SliceResult result;
            try
            {
                result = MeshCutter.Slice(chunk.mf.sharedMesh, planePointLocal, planeNormalLocal);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Slice failed on attempt {attempts}: {ex.Message}");
                continue;
            }

            if (result.positive == null || result.negative == null)
                continue;

            if (result.positive.vertexCount < 3 || result.negative.vertexCount < 3)
                continue;

            Chunk a = CreateChunkFromMesh(chunk.go, result.positive, chunk.mr, "_A", chunk.go.transform.parent);
            Chunk b = CreateChunkFromMesh(chunk.go, result.negative, chunk.mr, "_B", chunk.go.transform.parent);

            if (a == null || b == null)
                continue;

            chunks.RemoveAt(index);
            chunks.Add(a);
            chunks.Add(b);

            Undo.DestroyObjectImmediate(chunk.go);
        }

        Debug.Log($"Fracture finished. Piece count = {chunks.Count}, attempts = {attempts}");
        Selection.activeGameObject = chunks.Count > 0 ? chunks[0].go : null;
    }

    private void FractureCell()
    {
        if (target == null)
            return;

        if (cellCount < 2)
            cellCount = 2;

        var mf = target.GetComponent<MeshFilter>();
        var mr = target.GetComponent<MeshRenderer>();

        if (mf == null || mr == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("Target must have MeshFilter, MeshRenderer, and a valid mesh.");
            return;
        }

        Mesh sourceMesh = mf.sharedMesh;
        if (!sourceMesh.isReadable)
        {
            Debug.LogError("[MeshFracture] 源网格不可读，请在模型导入设置中启用 Read/Write Enabled。");
            return;
        }

        rng = useRandomSeed ? new System.Random() : new System.Random(seed);

        // 计算局部包围盒
        Bounds worldBounds = mr.bounds;
        Vector3 boundsMin = target.transform.InverseTransformPoint(worldBounds.min);
        Vector3 boundsMax = target.transform.InverseTransformPoint(worldBounds.max);
        Bounds localBounds = new Bounds(
            (boundsMin + boundsMax) * 0.5f,
            boundsMax - boundsMin
        );

        // 生成 Voronoi 细胞点
        List<Vector3> cellPoints = CellFracture.GenerateCellPoints(
            localBounds, cellCount, cellPointLimit, cellNoise, rng);

        if (cellPoints.Count < 2)
        {
            Debug.LogWarning("[MeshFracture] 生成的细胞点不足。");
            return;
        }

        // 为每个细胞点切割出碎片
        List<Mesh> fragments = new List<Mesh>();

        for (int i = 0; i < cellPoints.Count; i++)
        {
            Vector3 cellPoint = cellPoints[i];

            List<CellFracture.CutPlane> cellPlanes = CellFracture.GetCellPlanes(
                cellPoint, cellPoints, Vector3.one, cellGap);

            Mesh cellMesh = CellFracture.ClipMeshToCell(sourceMesh, cellPlanes, cellPoint);

            if (cellMesh != null && cellMesh.vertexCount >= 3)
            {
                fragments.Add(cellMesh);
            }
        }

        if (fragments.Count < 2)
        {
            Debug.LogWarning("[MeshFracture] 未能生成足够的碎片。");
            foreach (var m in fragments)
                if (m != null) DestroyImmediate(m);
            return;
        }

        // 创建碎片父物体
        GameObject parent = new GameObject(target.name + "_CellFracture");
        Undo.RegisterCreatedObjectUndo(parent, "Create Cell Fracture Parent");
        parent.transform.SetParent(target.transform.parent, false);
        parent.transform.position = target.transform.position;
        parent.transform.rotation = target.transform.rotation;
        parent.transform.localScale = target.transform.localScale;

        // 添加 MeshScale 组件
        MeshScale meshScale = parent.AddComponent<MeshScale>();
        meshScale.缩放 = 1f;

        // 创建每个碎片 GameObject
        for (int i = 0; i < fragments.Count; i++)
        {
            CreateCellFragment(
                fragments[i], mr.sharedMaterials,
                target, parent.transform, i);
        }

        // 销毁原物体
        if (destroyOriginal)
            Undo.DestroyObjectImmediate(target);

        Debug.Log($"Cell Fracture finished. Piece count = {fragments.Count}");
        Selection.activeGameObject = parent;
    }

    private void CreateCellFragment(
        Mesh mesh,
        Material[] materials,
        GameObject source,
        Transform parent,
        int index)
    {
        GameObject go = new GameObject(source.name + "_Cell_" + index);
        Undo.RegisterCreatedObjectUndo(go, "Create Cell Fragment");

        // 将 mesh 顶点偏移，使 pivot 对齐 mesh 几何中心
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        Vector3 meshCenter = mesh.bounds.center;
        if (meshCenter != Vector3.zero)
        {
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
                verts[v] -= meshCenter;
            mesh.SetVertices(verts);
            mesh.RecalculateBounds();
        }

        go.transform.SetParent(parent, false);
        go.transform.position = source.transform.TransformPoint(meshCenter);
        go.transform.rotation = source.transform.rotation;
        go.transform.localScale = source.transform.localScale * fragmentScale;

        go.layer = source.layer;
        go.tag = source.tag;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = materials;

        if (addMeshColliders)
        {
            bool addedMeshCollider = false;

            if (mesh != null && mesh.vertexCount >= 4 && mesh.triangles != null && mesh.triangles.Length >= 4)
            {
                try
                {
                    Mesh colliderMesh = CleanMeshForCollider(mesh);
                    Mesh useMesh = (colliderMesh != null) ? colliderMesh : mesh;

                    MeshCollider mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = useMesh;
                    mc.convex = true;
                    addedMeshCollider = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[MeshFracture] Convex MeshCollider failed for '{go.name}': {ex.Message}. Falling back to BoxCollider.");
                    MeshCollider bad = go.GetComponent<MeshCollider>();
                    if (bad != null)
                        DestroyImmediate(bad);
                }
            }

            if (!addedMeshCollider)
            {
                BoxCollider box = go.AddComponent<BoxCollider>();
                if (mesh != null)
                {
                    Bounds b = mesh.bounds;
                    box.center = b.center;
                    box.size = new Vector3(
                        Mathf.Max(b.size.x, 0.001f),
                        Mathf.Max(b.size.y, 0.001f),
                        Mathf.Max(b.size.z, 0.001f));
                }
                else
                {
                    box.center = Vector3.zero;
                    box.size = Vector3.one * 0.1f;
                }
            }
        }

        if (addRigidbodies)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
    }

    private Chunk CreateChunkFromExisting(GameObject source, Mesh mesh, MeshRenderer sourceRenderer, string suffix, Transform parentTransform)
    {
        // 这里不复制一个新对象，直接把原对象当作第一个 chunk 使用
        if (parentTransform != null)
            source.transform.SetParent(parentTransform, false);

        var chunk = new Chunk
        {
            go = source,
            mf = source.GetComponent<MeshFilter>(),
            mr = sourceRenderer
        };

        return chunk;
    }

    private Chunk CreateChunkFromMesh(GameObject source, Mesh mesh, MeshRenderer sourceRenderer, string suffix, Transform parentTransform)
    {
        GameObject go = new GameObject(source.name + suffix);
        Undo.RegisterCreatedObjectUndo(go, "Create Fracture Piece");

        // 将 mesh 顶点偏移，使 pivot 对齐 mesh 的几何中心，
        // 这样每个碎片的 localPosition 就不再是零，缩放时才能产生裂缝/扩散效果。
        mesh.RecalculateBounds();
        Vector3 meshCenter = mesh.bounds.center;
        if (meshCenter != Vector3.zero)
        {
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
                verts[v] -= meshCenter;
            mesh.SetVertices(verts);
            mesh.RecalculateBounds();
        }

        if (parentTransform != null)
            go.transform.SetParent(parentTransform, false);
        else
            go.transform.SetParent(source.transform.parent, false);

        // 把 GameObject 放在 mesh 几何中心对应的世界坐标上
        go.transform.position = source.transform.TransformPoint(meshCenter);
        go.transform.rotation = source.transform.rotation;
        go.transform.localScale = source.transform.localScale * fragmentScale;

        go.layer = source.layer;
        go.tag = source.tag;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = sourceRenderer.sharedMaterials;

        if (addMeshColliders)
        {
            bool addedMeshCollider = false;

            // 尝试使用 MeshCollider（即使三角形数量超过 256，Unity/PhysX 也会自动生成简化凸包）
            if (mesh != null && mesh.vertexCount >= 4 && mesh.triangles != null && mesh.triangles.Length >= 4)
            {
                try
                {
                    // 清理网格以减少 PhysX 烹饪失败的风险
                    Mesh colliderMesh = CleanMeshForCollider(mesh);
                    Mesh useMesh = (colliderMesh != null) ? colliderMesh : mesh;

                    MeshCollider mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = useMesh;
                    mc.convex = true;
                    addedMeshCollider = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[MeshFracture] Convex MeshCollider failed for '{go.name}': {ex.Message}. Falling back to BoxCollider.");
                    MeshCollider bad = go.GetComponent<MeshCollider>();
                    if (bad != null)
                        DestroyImmediate(bad);
                }
            }

            if (!addedMeshCollider)
            {
                BoxCollider box = go.AddComponent<BoxCollider>();
                if (mesh != null)
                {
                    Bounds b = mesh.bounds;
                    box.center = b.center;
                    box.size = new Vector3(
                        Mathf.Max(b.size.x, 0.001f),
                        Mathf.Max(b.size.y, 0.001f),
                        Mathf.Max(b.size.z, 0.001f));
                }
                else
                {
                    box.center = Vector3.zero;
                    box.size = Vector3.one * 0.1f;
                }
            }
        }

        if (addRigidbodies)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        return new Chunk
        {
            go = go,
            mf = mf,
            mr = mr
        };
    }

    private int GetLargestChunkIndex(List<Chunk> chunks)
    {
        int bestIndex = -1;
        float bestSize = -1f;

        for (int i = 0; i < chunks.Count; i++)
        {
            Chunk c = chunks[i];
            if (c == null || c.go == null || c.mr == null)
                continue;

            Vector3 s = c.Bounds.size;
            float volume = s.x * s.y * s.z;

            if (volume > bestSize)
            {
                bestSize = volume;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private Vector3 GetPlaneNormalWorld(System.Random random, CutStyle style)
    {
        switch (style)
        {
            case CutStyle.Horizontal:
                return Vector3.up;
            case CutStyle.Vertical:
                float angle = (float)(random.NextDouble() * Math.PI * 2.0);
                return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            case CutStyle.Slanted:
                return RandomUnitVector(random);
            case CutStyle.Random:
            default:
                return RandomUnitVector(random);
        }
    }

    private Vector3 RandomUnitVector(System.Random random)
    {
        double z = random.NextDouble() * 2.0 - 1.0;
        double t = random.NextDouble() * Math.PI * 2.0;
        double r = Math.Sqrt(1.0 - z * z);

        float x = (float)(r * Math.Cos(t));
        float y = (float)(r * Math.Sin(t));
        float zz = (float)z;

        return new Vector3(x, y, zz);
    }

    /// <summary>
    /// 清理网格：去重近似顶点并移除退化三角形，以减少凸包烹饪时的多边形数量。
    /// 与 CollisionFracture.CleanMeshForCollider 逻辑保持一致。
    /// </summary>
    private static Mesh CleanMeshForCollider(Mesh src)
    {
        if (src == null)
            return null;

        Vector3[] verts = src.vertices;
        int[] tris = src.triangles;
        if (verts == null || tris == null || verts.Length == 0 || tris.Length < 3)
            return null;

        float scale = 10000f; // 精度：0.0001
        var map = new Dictionary<IntVector3, int>();
        List<Vector3> newVerts = new List<Vector3>();

        int[] indexMap = new int[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 v = verts[i];
            IntVector3 key = new IntVector3(
                Mathf.RoundToInt(v.x * scale),
                Mathf.RoundToInt(v.y * scale),
                Mathf.RoundToInt(v.z * scale)
            );
            if (map.TryGetValue(key, out int idx))
            {
                indexMap[i] = idx;
            }
            else
            {
                idx = newVerts.Count;
                map[key] = idx;
                indexMap[i] = idx;
                newVerts.Add(v);
            }
        }

        List<int> newTris = new List<int>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            if (t + 2 >= tris.Length) break;
            int a = indexMap[tris[t]];
            int b = indexMap[tris[t + 1]];
            int c = indexMap[tris[t + 2]];
            if (a == b || b == c || a == c)
                continue;

            Vector3 va = newVerts[a];
            Vector3 vb = newVerts[b];
            Vector3 vc = newVerts[c];
            Vector3 cross = Vector3.Cross(vb - va, vc - va);
            if (cross.sqrMagnitude < 1e-8f)
                continue;

            newTris.Add(a);
            newTris.Add(b);
            newTris.Add(c);
        }

        if (newTris.Count < 3 || newVerts.Count < 4)
            return null;

        Mesh m = new Mesh();
        m.name = src.name + "_Clean";
        m.SetVertices(newVerts);
        m.SetTriangles(newTris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    /// <summary>
    /// 整数向量结构体，用于顶点去重，避免字符串拼接的 GC 分配。
    /// </summary>
    private struct IntVector3 : IEquatable<IntVector3>
    {
        public int x, y, z;

        public IntVector3(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(IntVector3 other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public override bool Equals(object obj)
        {
            return obj is IntVector3 other && Equals(other);
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
}