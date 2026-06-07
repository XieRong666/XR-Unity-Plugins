using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using SimpleFracture;

/// <summary>
/// 基于 Voronoi 细胞的运行时网格破碎组件。
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Collider))]
public class CellFracture : MonoBehaviour
{
    [Header("细胞设置")]
    [Tooltip("生成的碎片数量。数值越高碎片越多，但运行时切割和物理开销也越高。")]
    [Range(2, 50)]
    public int 细胞数量 = 8;

    [Tooltip("给细胞种子点添加随机偏移。0 表示均匀随机点，值越高碎片形状越不规则。")]
    [Range(0f, 1f)]
    public float 源点噪声 = 0f;

    [Tooltip("控制细胞切割方向的非均匀缩放。可用来生成被拉长或压扁的碎片形状。")]
    public Vector3 细胞缩放 = Vector3.one;

    [Tooltip("碎片之间预留的微小间隙。适当增大可减少物理重叠，但过大可能让裂缝明显。")]
    [Range(0f, 0.01f)]
    public float 间隙 = 0.001f;

    [Tooltip("每个细胞最多使用的最近切割平面数。0 表示使用全部平面；较小值破碎更快但形状近似度更低。")]
    [Range(0, 64)]
    public int 最大切割平面数 = 12;

    [Header("破碎触发")]
    [Tooltip("碰撞相对速度平方达到该阈值平方时触发破碎。值越高越不容易破碎。")]
    public float 影响阈值 = 2f;

    [Tooltip("启用后，同一个物体只会响应第一次有效破碎触发。")]
    public bool 只破坏一次 = true;

    [Header("碎片设置")]
    [Tooltip("破碎后复制到每个子碎片上的组件实例。会复制这些组件的可序列化字段值。")]
    public Component[] 子物组件 = new Component[0];

    [Tooltip("破碎后添加到每个子碎片上的组件类型名。填写 C# 类名或完整类型名。")]
    public string[] 子物脚本类型 = new string[0];

    [Tooltip("破碎完成后是否销毁原始物体。关闭后原物体会保留在场景中。")]
    public bool 是否销毁原体 = true;

    [Tooltip("所有碎片生成后的统一缩放系数。小于 1 可拉开裂缝，大于 1 会放大碎片。")]
    [Range(0.1f, 5f)]
    public float 碎片缩放 = 1f;

    [Tooltip("是否为每个碎片添加凸 MeshCollider。关闭后碎片不会自动获得网格碰撞。")]
    public bool 添加网格碰撞器 = true;

    [Tooltip("是否为每个碎片添加 Rigidbody。关闭后碎片不会自动参与刚体物理。")]
    public bool 添加刚体 = true;

    [Tooltip("启用后不添加 Rigidbody，使碎片保持静态位置，适合只展示破碎结果。")]
    public bool 静态破碎 = false;

    [Tooltip("启用后按碎片体积分配质量；关闭后每个碎片使用相同质量。")]
    public bool 按体积分配质量 = true;

    [Tooltip("总质量。按体积分配质量时，会按体积比例分摊到所有碎片。")]
    public float 总质量 = 10f;

    [Header("递归破碎")]
    [Tooltip("自动递归破碎的最大层数。0 表示不自动递归；值越高碎片数量增长越快。")]
    [Range(0, 5)]
    public int 递归次数 = 0;

    [Tooltip("每个子碎片继续自动破碎的概率。仅在递归次数大于 0 时生效。")]
    [Range(0f, 1f)]
    public float 递归概率 = 0.25f;


    // 标记此组件是否为运行时生成的碎片
    [NonSerialized]
    public bool 已生成 = false;

    private bool fractured;
    private System.Random random;

    private void Awake()
    {
        random = new System.Random(
            Environment.TickCount
            ^ (gameObject.GetHashCode() * 73856093)
            ^ (transform.position.GetHashCode())
        );
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (fractured && 只破坏一次)
            return;

        if (已生成 && 递归次数 <= 0)
            return;

        if (collision.relativeVelocity.sqrMagnitude < 影响阈值 * 影响阈值)
            return;

        Fracture();
        fractured = true;
    }

    /// <summary>
    public void Fracture()
    {
        if (fractured && 只破坏一次)
            return;
        if (已生成 && 递归次数 <= 0)
            return;
        fractured = true;

        MeshFilter mf = GetComponent<MeshFilter>();
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null)
            return;

        Mesh sourceMesh = mf.sharedMesh;
        if (!sourceMesh.isReadable)
        {
            Debug.LogError("[CellFracture] 源网格不可读，请在模型导入设置中启用 Read/Write Enabled。", this);
            return;
        }

        Bounds bounds = mr.bounds;
        Vector3 boundsMin = transform.InverseTransformPoint(bounds.min);
        Vector3 boundsMax = transform.InverseTransformPoint(bounds.max);
        Bounds localBounds = new Bounds(
            (boundsMin + boundsMax) * 0.5f,
            boundsMax - boundsMin
        );

        List<Vector3> cellPoints = GenerateCellPoints(localBounds);

        if (cellPoints.Count < 2)
        {
            Debug.LogWarning("[CellFracture] 生成的细胞点不足。", this);
            return;
        }

        List<Mesh> fragments = new List<Mesh>();
        List<Vector3> fragmentCenters = new List<Vector3>();

        for (int i = 0; i < cellPoints.Count; i++)
        {
            Vector3 cellPoint = cellPoints[i];

            List<CutPlane> cellPlanes = GetCellPlanes(cellPoint, cellPoints);

            // 用所有平面依次切割网格，保留细胞点所在侧
            Mesh cellMesh = ClipMeshToCell(sourceMesh, cellPlanes, cellPoint);

            if (cellMesh != null && cellMesh.vertexCount >= 3)
            {
                fragments.Add(cellMesh);
                fragmentCenters.Add(cellPoint);
            }
        }

        if (fragments.Count < 2)
        {
            Debug.LogWarning("[CellFracture] 未能生成足够的碎片。", this);
            foreach (var m in fragments)
                if (m != null) Destroy(m);
            return;
        }

        GameObject rootObj = new GameObject(gameObject.name + "_CellFracture");
        Transform fragmentsRoot = rootObj.transform;
        fragmentsRoot.position = transform.position;
        fragmentsRoot.rotation = transform.rotation;
        fragmentsRoot.localScale = transform.localScale;
        fragmentsRoot.SetParent(transform.parent, true);

        MeshScale meshScale = rootObj.AddComponent<MeshScale>();
        meshScale.缩放 = 1f;

        // 计算碎片体积用于质量分配
        List<float> fragmentVolumes = null;
        float totalVolume = 0f;
        if (按体积分配质量)
        {
            fragmentVolumes = new List<float>();
            foreach (var frag in fragments)
            {
                float vol = CalculateMeshVolume(frag);
                fragmentVolumes.Add(vol);
                totalVolume += vol;
            }
        }

        for (int i = 0; i < fragments.Count; i++)
        {
            float mass = 1f;
            if (按体积分配质量 && totalVolume > 0f)
            {
                mass = Mathf.Max(0.01f, (fragmentVolumes[i] / totalVolume) * 总质量);
            }

            CreateFracturePiece(
                fragments[i], mr.sharedMaterials,
                fragmentCenters[i], fragmentsRoot, i, mass
            );
        }

        if (是否销毁原体)
            Destroy(gameObject);
    }

    /// <summary>
    private List<Vector3> GenerateCellPoints(Bounds localBounds)
    {
        return GenerateCellPoints(localBounds, 细胞数量, 源点噪声, random);
    }

    /// <summary>
    private List<CutPlane> GetCellPlanes(Vector3 cellPoint, List<Vector3> allPoints)
    {
        return GetCellPlanes(cellPoint, allPoints, 细胞缩放, 间隙, 最大切割平面数);
    }

    /// <summary>
    private void CreateFracturePiece(
        Mesh mesh,
        Material[] materials,
        Vector3 cellCenter,
        Transform parent,
        int index,
        float mass)
    {
        GameObject go = new GameObject(gameObject.name + "_Cell_" + index);

        // 确保网格有正确的法线和包围盒
        Vector3 meshCenter = mesh.bounds.center;
        if (meshCenter != Vector3.zero)
        {
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
                verts[v] -= meshCenter;
            mesh.SetVertices(verts);
            mesh.RecalculateBounds();
        }

        go.transform.position = transform.TransformPoint(meshCenter);
        go.transform.rotation = transform.rotation;
        go.transform.localScale = transform.localScale * 碎片缩放;
        go.transform.SetParent(parent, true);

        go.layer = gameObject.layer;
        go.tag = gameObject.tag;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = materials;

        if (添加网格碰撞器)
        {
            if (mesh != null && mesh.vertexCount >= 4 && mesh.triangles != null
                && mesh.triangles.Length >= 4 && mesh.bounds.size.magnitude > 0.0001f)
            {
                try
                {
                    MeshCollider mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CellFracture] MeshCollider 创建失败于碎片 {index}，回退到 BoxCollider: {ex.Message}", go);
                    MeshCollider bad = go.GetComponent<MeshCollider>();
                    if (bad != null) Destroy(bad);
                    AddFallbackBoxCollider(go, mesh);
                }
            }
            else
            {
                AddFallbackBoxCollider(go, mesh);
            }
        }

        // 添加刚体
        if (添加刚体 && !静态破碎)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        CellFracture childFracture = go.AddComponent<CellFracture>();
        childFracture.细胞数量 = this.细胞数量;
        childFracture.源点噪声 = this.源点噪声;
        childFracture.细胞缩放 = this.细胞缩放;
        childFracture.间隙 = this.间隙;
        childFracture.最大切割平面数 = this.最大切割平面数;
        childFracture.影响阈值 = this.影响阈值;
        childFracture.只破坏一次 = this.只破坏一次;
        childFracture.是否销毁原体 = this.是否销毁原体;
        childFracture.添加网格碰撞器 = this.添加网格碰撞器;
        childFracture.添加刚体 = this.添加刚体;
        childFracture.静态破碎 = this.静态破碎;
        childFracture.按体积分配质量 = this.按体积分配质量;
        childFracture.总质量 = mass;
        childFracture.碎片缩放 = this.碎片缩放;
        childFracture.递归次数 = Mathf.Max(0, this.递归次数 - 1);
        childFracture.递归概率 = this.递归概率;
        childFracture.已生成 = true;
        childFracture.子物组件 = this.子物组件;
        childFracture.子物脚本类型 = this.子物脚本类型;

        CopyComponentsToChild(go);

        if (this.递归次数 > 0 && this.递归概率 > 0f)
        {
            if (random.NextDouble() < this.递归概率)
            {
                childFracture.StartCoroutine(DeferredFracture(childFracture));
            }
        }
    }

    private System.Collections.IEnumerator DeferredFracture(CellFracture child)
    {
        yield return null;
        if (child != null && child.gameObject != null)
        {
            child.Fracture();
        }
    }

    /// <summary>
    private void CopyComponentsToChild(GameObject target)
    {
        var addedTypes = new HashSet<Type>();

        // 第一遍：处理组件实例（拖入的场景组件，可复制配置值）
        if (子物组件 != null)
        {
            foreach (var source in 子物组件)
            {
                if (source == null)
                    continue;

                var type = source.GetType();

                if (type == typeof(Transform) || type == typeof(CellFracture))
                    continue;

                if (target.GetComponent(type) != null)
                    continue;

                Component copy = target.AddComponent(type);
                CopySerializedFields(source, copy, type);
                addedTypes.Add(type);
            }
        }

        if (子物脚本类型 != null)
        {
            foreach (var typeName in 子物脚本类型)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                Type type = ResolveComponentType(typeName.Trim());
                if (type == null)
                {
                    Debug.LogWarning($"[CellFracture] 无法解析组件类型: \"{typeName}\"，请确认类名正确。", this);
                    continue;
                }

                if (type == typeof(Transform) || type == typeof(CellFracture))
                    continue;

                if (addedTypes.Contains(type) || target.GetComponent(type) != null)
                    continue;

                target.AddComponent(type);
                addedTypes.Add(type);
            }
        }
    }

    /// <summary>
    /// 根据类型名称解析 System.Type，支持简单类名和完整限定名，
    /// 会搜索所有已加载的程序集。
    /// </summary>
    private static Type ResolveComponentType(string typeName)
    {
        Type type = Type.GetType(typeName);
        if (type != null && typeof(Component).IsAssignableFrom(type))
            return type;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in assemblies)
        {
            // 先按完整名称匹配
            type = asm.GetType(typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;

            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == typeName && typeof(Component).IsAssignableFrom(t))
                        return t;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    private static void CopySerializedFields(Component source, Component target, Type type)
    {
        const BindingFlags flags = BindingFlags.Public
                                  | BindingFlags.NonPublic
                                  | BindingFlags.Instance
                                  | BindingFlags.FlattenHierarchy;

        var fields = type.GetFields(flags);
        foreach (var field in fields)
        {
            bool isSerialized = field.IsDefined(typeof(SerializeField), false)
                || (field.IsPublic && !field.IsDefined(typeof(NonSerializedAttribute), false));

            if (!isSerialized)
                continue;

            if (field.IsInitOnly)
                continue;

            try
            {
                object value = field.GetValue(source);
                field.SetValue(target, value);
            }
            catch
            {
            }
        }
    }

    private static void AddFallbackBoxCollider(GameObject go, Mesh mesh)
    {
        BoxCollider box = go.AddComponent<BoxCollider>();
        if (mesh != null)
        {
            box.center = mesh.bounds.center;
            Vector3 extents = mesh.bounds.extents;
            box.size = new Vector3(
                Mathf.Max(extents.x * 2f, 0.001f),
                Mathf.Max(extents.y * 2f, 0.001f),
                Mathf.Max(extents.z * 2f, 0.001f)
            );
        }
        else
        {
            box.center = Vector3.zero;
            box.size = Vector3.one * 0.1f;
        }
    }

    public struct CutPlane
    {
        public Vector3 point;
        public Vector3 normal;
    }

    #region Static Voronoi Utilities

    /// <summary>
    public static List<Vector3> GenerateCellPoints(
        Bounds localBounds, int cellCount, float noise, System.Random random)
    {
        List<Vector3> points = new List<Vector3>();
        Vector3 min = localBounds.min;
        Vector3 size = localBounds.size;

        for (int i = 0; i < cellCount; i++)
        {
            float rx = (float)random.NextDouble();
            float ry = (float)random.NextDouble();
            float rz = (float)random.NextDouble();

            Vector3 p = new Vector3(
                min.x + rx * size.x,
                min.y + ry * size.y,
                min.z + rz * size.z
            );

            if (noise > 0f)
            {
                Vector3 noiseVec = RandomUnitVector(random) * noise * size.magnitude * 0.15f;
                p += noiseVec;
                p.x = Mathf.Clamp(p.x, min.x, min.x + size.x);
                p.y = Mathf.Clamp(p.y, min.y, min.y + size.y);
                p.z = Mathf.Clamp(p.z, min.z, min.z + size.z);
            }

            points.Add(p);
        }

        // 去重
        if (points.Count > 1)
        {
            float quantize = 10000f;
            var seen = new Dictionary<long, Vector3>();
            foreach (var p in points)
            {
                int qx = Mathf.RoundToInt(p.x * quantize);
                int qy = Mathf.RoundToInt(p.y * quantize);
                int qz = Mathf.RoundToInt(p.z * quantize);
                long key = ((long)qx << 42) | ((long)qy << 21) | (long)qz;
                if (!seen.ContainsKey(key))
                    seen[key] = p;
            }
            points = new List<Vector3>(seen.Values);
        }

        return points;
    }

    public static List<Vector3> GenerateCellPoints(
        Bounds localBounds, int cellCount, int pointLimit, float noise, System.Random random)
    {
        int limitedCellCount = pointLimit > 0 ? Mathf.Min(cellCount, pointLimit) : cellCount;
        return GenerateCellPoints(localBounds, limitedCellCount, noise, random);
    }

    /// <summary>
    public static List<CutPlane> GetCellPlanes(
        Vector3 cellPoint, List<Vector3> allPoints, Vector3 cellScale, float gap)
    {
        return GetCellPlanes(cellPoint, allPoints, cellScale, gap, 0);
    }

    public static List<CutPlane> GetCellPlanes(
        Vector3 cellPoint, List<Vector3> allPoints, Vector3 cellScale, float gap, int maxPlanes)
    {
        var neighbors = new List<(float distSq, Vector3 point)>();
        foreach (var p in allPoints)
        {
            if ((p - cellPoint).sqrMagnitude < 1e-8f)
                continue;
            neighbors.Add(((p - cellPoint).sqrMagnitude, p));
        }
        neighbors.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        List<CutPlane> planes = new List<CutPlane>();
        int planeCount = maxPlanes > 0 ? Mathf.Min(maxPlanes, neighbors.Count) : neighbors.Count;

        for (int i = 0; i < planeCount; i++)
        {
            Vector3 otherPoint = neighbors[i].point;
            Vector3 rawNormal = otherPoint - cellPoint;
            float nlength = rawNormal.magnitude;
            if (nlength < 1e-8f)
                continue;

            Vector3 normal = rawNormal / nlength;

            if (cellScale != Vector3.one)
            {
                normal.x *= cellScale.x;
                normal.y *= cellScale.y;
                normal.z *= cellScale.z;
                normal.Normalize();
            }

            Vector3 midPoint = (cellPoint + otherPoint) * 0.5f;
            Vector3 planePoint = midPoint + normal * gap;

            planes.Add(new CutPlane { point = planePoint, normal = normal });
        }

        return planes;
    }

    /// <summary>
    public static Mesh ClipMeshToCell(
        Mesh sourceMesh, List<CutPlane> cellPlanes, Vector3 cellCenter)
    {
        if (cellPlanes.Count == 0)
            return null;

        Mesh current = UnityEngine.Object.Instantiate(sourceMesh);
        current.name = sourceMesh.name + "_temp";

        for (int i = 0; i < cellPlanes.Count; i++)
        {
            CutPlane plane = cellPlanes[i];

            Bounds meshBounds = current.bounds;
            Vector3 n = plane.normal;
            Vector3 mostPositive = new Vector3(
                n.x > 0 ? meshBounds.max.x : meshBounds.min.x,
                n.y > 0 ? meshBounds.max.y : meshBounds.min.y,
                n.z > 0 ? meshBounds.max.z : meshBounds.min.z
            );
            Vector3 mostNegative = new Vector3(
                n.x > 0 ? meshBounds.min.x : meshBounds.max.x,
                n.y > 0 ? meshBounds.min.y : meshBounds.max.y,
                n.z > 0 ? meshBounds.min.z : meshBounds.max.z
            );
            float distMostPositive = Vector3.Dot(n, mostPositive - plane.point);
            float distMostNegative = Vector3.Dot(n, mostNegative - plane.point);

            bool cellOnPositive = Vector3.Dot(n, cellCenter - plane.point) >= 0f;
            if (cellOnPositive)
            {
                if (distMostNegative >= 0f)
                    continue;
            }
            else
            {
                if (distMostPositive <= 0f)
                    continue;
            }

            MeshCutter.SliceResult result;
            try
            {
                result = MeshCutter.Slice(current, plane.point, plane.normal, cellOnPositive, !cellOnPositive);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CellFracture] Slice 失败: {ex.Message}");
                continue;
            }

            if (result.positive == null && result.negative == null)
                continue;

            Mesh keep = cellOnPositive ? result.positive : result.negative;
            Mesh discard = cellOnPositive ? result.negative : result.positive;

            if (discard != null)
                DestroyMesh(discard);

            if (keep == null || keep.vertexCount < 3)
            {
                DestroyMesh(current);
                return null;
            }

            DestroyMesh(current);
            current = keep;
            current.name = sourceMesh.name + "_temp";
        }

        return current;
    }

    private static void DestroyMesh(UnityEngine.Object obj)
    {
        if (obj == null)
            return;
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(obj);
        else
            UnityEngine.Object.DestroyImmediate(obj);
    }

    /// <summary>
    public static float CalculateMeshVolume(Mesh mesh)
    {
        if (mesh == null || mesh.triangles == null || mesh.triangles.Length < 3)
            return 0f;

        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        float signedVolume = 0f;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 a = verts[tris[i]];
            Vector3 b = verts[tris[i + 1]];
            Vector3 c = verts[tris[i + 2]];
            signedVolume += Vector3.Dot(Vector3.Cross(a, b), c);
        }

        return Mathf.Abs(signedVolume) / 6f;
    }

    /// <summary>
    public static Vector3 RandomUnitVector(System.Random random)
    {
        double z = random.NextDouble() * 2.0 - 1.0;
        double t = random.NextDouble() * Math.PI * 2.0;
        double r = Math.Sqrt(1.0 - z * z);

        return new Vector3(
            (float)(r * Math.Cos(t)),
            (float)(r * Math.Sin(t)),
            (float)z
        );
    }

    #endregion
}
