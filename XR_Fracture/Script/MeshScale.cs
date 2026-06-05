using UnityEngine;

/// <summary>
/// 统一滑动控制所有子物体的缩放大小。
/// 挂载在碎片根物体上，拖动滑动条即可实时调节每个子物体的 localScale。
///
/// 关闭「改变中心位置」：整体缩放 — 子物体围绕根物体中心扩散/收缩，物体之间不出现裂缝。
/// 打开「改变中心位置」：各自缩放 — 每个子物体以自身为原点缩放，物体之间会出现裂缝。
/// </summary>
[ExecuteInEditMode]
public class MeshScale : MonoBehaviour
{
    [Header("缩放")]
    [Tooltip("子物体统一缩放系数（1 = 原始大小）。")]
    [Range(0.1f, 5f)]
    public float 缩放 = 1f;

    [Tooltip("打开：每个子物体以自身为原点缩放，物体之间会出现裂缝。\n关闭：整体缩放，子物体围绕根物体中心扩散/收缩，无裂缝。")]
    public bool 改变中心位置 = false;

    private float _prevScale = 1f;
    private bool _prevCenterScaling;
    private Vector3[] _originalScales;
    private Vector3[] _originalPositions;
    private Transform[] _children;
    private bool _initialized;

    private void OnEnable()
    {
        _initialized = false;
    }

    private void Start()
    {
        InitIfNeeded();
    }

    private void InitIfNeeded()
    {
        if (_initialized && _children != null && _children.Length == transform.childCount)
        {
            bool changed = false;
            for (int i = 0; i < _children.Length; i++)
            {
                if (_children[i] != transform.GetChild(i))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
                return;
        }

        int count = transform.childCount;
        _children = new Transform[count];
        _originalScales = new Vector3[count];
        _originalPositions = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            _children[i] = transform.GetChild(i);
            _originalScales[i] = _children[i].localScale;
            _originalPositions[i] = _children[i].localPosition;
        }

        _prevScale = 缩放;
        _prevCenterScaling = 改变中心位置;
        _initialized = true;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!_initialized)
            InitIfNeeded();

        if (_children == null || _originalScales == null)
            return;

        if (!Mathf.Approximately(缩放, _prevScale) || 改变中心位置 != _prevCenterScaling)
        {
            ApplyScale();
            _prevScale = 缩放;
            _prevCenterScaling = 改变中心位置;
        }
    }
#endif

    private void Update()
    {
        if (!_initialized)
            InitIfNeeded();

        if (!Mathf.Approximately(缩放, _prevScale) || 改变中心位置 != _prevCenterScaling)
        {
            ApplyScale();
            _prevScale = 缩放;
            _prevCenterScaling = 改变中心位置;
        }
    }

    /// <summary>
    /// 将所有子物体的 localScale 设置为 原始缩放 × 缩放系数。
    /// 关闭「改变中心位置」时：整体缩放，同时调整 localPosition 使碎片围绕根物体中心扩散/收缩（无裂缝）。
    /// 打开「改变中心位置」时：各自缩放，保持原始 localPosition（每个碎片以自身为原点缩放，出现裂缝）。
    /// </summary>
    private void ApplyScale()
    {
        if (_children == null || _originalScales == null)
            return;

        for (int i = 0; i < _children.Length; i++)
        {
            if (_children[i] == null)
                continue;

            _children[i].localScale = _originalScales[i] * 缩放;

            if (!改变中心位置)
                _children[i].localPosition = _originalPositions[i] * 缩放;
            else
                _children[i].localPosition = _originalPositions[i];
        }
    }

    /// <summary>
    /// 刷新缓存的子物体列表和原始数据（在外部添加/删除子物体后调用）。
    /// </summary>
    public void RefreshChildren()
    {
        _initialized = false;
        InitIfNeeded();
        ApplyScale();
    }
}
