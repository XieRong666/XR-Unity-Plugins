using System;
using System.Collections.Generic;
using UnityEngine;
using SimpleFracture;

/// <summary>
/// Cell Fracture — 基于 Voronoi 图（泰森多边形）的网格破碎组件。
///
/// 算法原理（仿照 Blender Cell Fracture 插件）：
/// 1. 在网格包围盒内生成 N 个随机细胞点（种子点）
/// 2. 对每个细胞点，计算它与其他所有点之间的平分平面（bisecting plane）
/// 3. 用这些平分平面依次切割源网格，每次保留细胞点所在的一侧
/// 4. 切割完毕后得到的网格即该 Voronoi 细胞与源网格的交集
///
/// Cell Fracture 将整个物体划分为 Voronoi 细胞碎片，适合整体破碎效果。
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Collider))]
public class CellFracture : MonoBehaviour
{
    [Header("细胞设置")]
    [Tooltip("生成的碎片（细胞）数量。")]
    [Range(2, 50)]
    public int 细胞数量 = 8;

    [Tooltip("限制使用的种子点数量（0 = 不限制）。")]
    [Range(0, 5000)]
    public int 源点限制 = 100;

    [Tooltip("种子点位置的随机扰动程度（0-1）。")]
    [Range(0f, 1f)]
    public float 源点噪声 = 0f;

    [Tooltip("细胞形状的非均匀缩放（X/Y/Z），产生拉长的碎片。")]
    public Vector3 细胞缩放 = Vector3.one;

    [Tooltip("碎片之间的间隙，用于提高物理稳定性。")]
    [Range(0f, 0.01f)]
    public float 间隙 = 0.001f;

    [Header("破碎触发")]
    [Tooltip("触发破碎所需的最小冲击力。")]
    public float 影响阈值 = 2f;

    [Tooltip("为真时，仅第一次有效碰撞会触发破碎。")]
    public bool 只破坏一次 = true;

    [Header("碎片设置")]
    [Tooltip("为真时，破碎后移除原始物体。")]
    public bool 是否销毁原体 = true;

    [Tooltip("碎片生成后的统一缩放系数（1 = 原始大小）。")]
    [Range(0.1f, 5f)]
    public float 碎片缩放 = 1f;

    [Tooltip("启用破碎碎片的网格碰撞器。")]
    public bool 添加网格碰撞器 = true;

    [Tooltip("启用破碎碎片的刚体。")]
    public bool 添加刚体 = true;

    [Tooltip("启用静态破碎，使碎片保持原地而不飞出。")]
    public bool 静态破碎 = false;

    [Tooltip("按体积分配质量。为真则按体积比例分配，为假则平均分配。")]
    public bool 按体积分配质量 = true;

    [Tooltip("总质量（按体积分配时使用）。")]
    public float 总质量 = 10f;

    [Header("递归破碎")]
    [Tooltip("递归破碎次数（0 = 不递归，子碎片可继续破碎）。")]
    [Range(0, 5)]
    public int 递归次数 = 0;

    [Tooltip("递归时的源点限制。")]
    [Range(0, 5000)]
    public int 递归源点限制 = 8;

    [Tooltip("递归概率（0-1）。")]
    [Range(0f, 1f)]
    public float 递归概率 = 0.25f;

    [Tooltip("递归目标选择：随机 / 小碎片优先 / 大碎片优先。")]
    public RecursionTarget 递归目标 = RecursionTarget.小碎片优先;

    public enum RecursionTarget
    {
        随机,
        小碎片优先,
        大碎片优先
    }

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
    /// 手动触发破碎（可从其他脚本调用或通过 UI 按钮调用）。
    /// </summary>
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

        // 生成细胞点
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
            Debug.LogWarning("[CellFracture] 生成的细胞点不足。");
            return;
        }

        // 为每个细胞点切割出碎片
        List<Mesh> fragments = new List<Mesh>();
        List<Vector3> fragmentCenters = new List<Vector3>();

        for (int i = 0; i < cellPoints.Count; i++)
        {
            Vector3 cellPoint = cellPoints[i];

            // 计算该细胞相对于其他所有点的平分平面
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
            Debug.LogWarning("[CellFracture] 未能生成足够的碎片。");
            foreach (var m in fragments)
                if (m != null) Destroy(m);
            return;
        }

        // 创建碎片父物体
        GameObject rootObj = new GameObject(gameObject.name + "_CellFracture");
        Transform fragmentsRoot = rootObj.transform;
        fragmentsRoot.position = transform.position;
        fragmentsRoot.rotation = transform.rotation;
        fragmentsRoot.localScale = transform.localScale;
        fragmentsRoot.SetParent(transform.parent, true);

        // 添加 MeshScale 组件，用于滑动统一控制所有子碎片缩放
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

        // 销毁原物体
        if (是否销毁原体)
            Destroy(gameObject);
    }

    /// <summary>
    /// 在网格的局部包围盒内生成细胞种子点（实例方法，转发到静态版本）。
    /// </summary>
    private List<Vector3> GenerateCellPoints(Bounds localBounds)
    {
        return GenerateCellPoints(localBounds, 细胞数量, 源点限制, 源点噪声, random);
    }

    /// <summary>
    /// 计算给定细胞点相对于其他所有点的平分平面列表（实例方法，转发到静态版本）。
    /// </summary>
    private List<CutPlane> GetCellPlanes(Vector3 cellPoint, List<Vector3> allPoints)
    {
        return GetCellPlanes(cellPoint, allPoints, 细胞缩放, 间隙);
    }

    /// <summary>
    /// 创建碎片 GameObject。
    /// </summary>
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
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // 将 mesh 顶点偏移，使 pivot 对齐 mesh 的几何中心，
        // 这样每个碎片的 localPosition 就不再是零，缩放时才能产生裂缝/扩散效果。
        Vector3 meshCenter = mesh.bounds.center;
        if (meshCenter != Vector3.zero)
        {
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
                verts[v] -= meshCenter;
            mesh.SetVertices(verts);
            mesh.RecalculateBounds();
        }

        // 把 GameObject 放在 mesh 几何中心对应的世界坐标上
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

        // 添加碰撞器
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

        // 添加子 CellFracture 组件用于递归破碎
        CellFracture childFracture = go.AddComponent<CellFracture>();
        childFracture.细胞数量 = this.细胞数量;
        childFracture.源点限制 = this.递归源点限制;
        childFracture.源点噪声 = this.源点噪声;
        childFracture.细胞缩放 = this.细胞缩放;
        childFracture.间隙 = this.间隙;
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
        childFracture.递归源点限制 = this.递归源点限制;
        childFracture.递归概率 = this.递归概率;
        childFracture.递归目标 = this.递归目标;
        childFracture.已生成 = true;

        // 对子碎片按概率递归破碎
        if (this.递归次数 > 0 && this.递归概率 > 0f)
        {
            if (random.NextDouble() < this.递归概率)
            {
                // 延迟一帧触发递归破碎，避免在同一帧内产生过多碎片
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
    /// 在局部包围盒内生成细胞种子点。
    /// </summary>
    public static List<Vector3> GenerateCellPoints(
        Bounds localBounds, int cellCount, int pointLimit, float noise, System.Random random)
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

        // 限制点数
        if (pointLimit > 0 && points.Count > pointLimit)
        {
            for (int i = points.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var tmp = points[i];
                points[i] = points[j];
                points[j] = tmp;
            }
            points.RemoveRange(pointLimit, points.Count - pointLimit);
        }

        return points;
    }

    /// <summary>
    /// 计算给定细胞点相对于其他所有点的平分平面列表。
    /// </summary>
    public static List<CutPlane> GetCellPlanes(
        Vector3 cellPoint, List<Vector3> allPoints, Vector3 cellScale, float gap)
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

        foreach (var (_, otherPoint) in neighbors)
        {
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
    /// 用一组平面依次切割网格，每次保留细胞点所在的一侧。
    /// </summary>
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
                result = MeshCutter.Slice(current, plane.point, plane.normal);
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
    /// 使用有符号四面体法计算封闭网格的精确体积。
    /// </summary>
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
    /// 生成随机单位向量（均匀分布在球面上）。
    /// </summary>
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
