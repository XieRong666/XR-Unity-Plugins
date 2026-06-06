using System.Collections;
using UnityEngine;

public class DisappearMesh : MonoBehaviour
{
    private CellFracture cellFracture;

    [Header("死亡时间")]
    public float DeathTime = 2f;

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

            float t = timer / DeathTime;

            transform.localScale = Vector3.Lerp(
                startScale,
                Vector3.zero,
                t
            );
            //yield return new WaitForSeconds(DeathTime);
            yield return null;
        }

        Destroy(gameObject);
    }
}