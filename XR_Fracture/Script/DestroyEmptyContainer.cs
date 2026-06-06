using UnityEngine;

/// <summary>
/// 自动销毁空的 *_CellFracture 容器物体。
/// 将此脚本挂载到原物体上，破碎时会通过「子物组件」自动传递给每个子碎片。
/// 当碎片被销毁时（如 MeshDissolve 消融完毕），级联向上清理已无子物体的容器。
/// </summary>
public class DestroyEmptyContainer : MonoBehaviour
{
    private Transform cachedParent;

    void Awake()
    {
        cachedParent = transform.parent;
    }

    void OnDestroy()
    {
        Transform check = null;

        // 自己在被销毁前已从父物体脱离（如 MeshDissolve 中调用了 SetParent(null)）
        if (transform.parent == null && cachedParent != null && cachedParent.childCount == 0)
        {
            check = cachedParent;
        }
        // 自己还挂在父物体上，判断自己是不是最后一个子物体
        else if (transform.parent != null && transform.parent.childCount <= 1)
        {
            check = transform.parent;
        }

        if (check != null)
        {
            CleanupChain(check);
        }
    }

    private static void CleanupChain(Transform container)
    {
        while (container != null
               && container.name.Contains("_CellFracture")
               && container.childCount == 0)
        {
            Transform next = container.parent;
            container.SetParent(null);   // 立即从上层移除，使级联 childCount 判断正确
            Destroy(container.gameObject);
            container = next;
        }
    }
}
