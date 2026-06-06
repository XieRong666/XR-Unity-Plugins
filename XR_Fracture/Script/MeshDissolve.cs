using System.Collections;
using UnityEngine;

public class MeshDissolve : MonoBehaviour
{
    private CellFracture cellFracture;

    [Header("死亡时间")]
    public float DeathTime = 2f;

    [Header("缩放曲线")]
    [Tooltip("控制碎片消融速度随时间的变化曲线。横轴为归一化时间 (0→1)，纵轴为缩放比例 (1→0)。默认线性衰减。")]
    public AnimationCurve 消融曲线 = AnimationCurve.Linear(0f, 1f, 1f, 0f);

    private bool isDeath = true;

    void Start()
    {
        cellFracture = GetComponent<CellFracture>();
    }

    void Update()
    {
        if (cellFracture.递归次数 == 0 && isDeath)
        {
            isDeath = false;
            StartCoroutine(Death());
        }
    }

    IEnumerator Death()
    {
        Vector3 startScale = transform.localScale;
        float timer = 0f;

        while (timer < DeathTime)
        {
            timer += Time.deltaTime;

            float t = Mathf.Clamp01(timer / DeathTime);
            float curveValue = Mathf.Clamp01(消融曲线.Evaluate(t));

            transform.localScale = startScale * curveValue;

            yield return null;
        }

        // 先从父物体脱离（使 childCount 立即减少），再销毁自己
        transform.SetParent(null);
        Destroy(gameObject);
    }
}