using UnityEngine;

/// <summary>
/// 地图场景中的怪物显隐：只有手电真正照到（在扇形内、距离内、且无墙遮挡）时才显示 Sprite。
/// 地图墙体尚未生成 ShadowCaster2D 时，这个脚本可以避免玩家隔着墙看到红色怪物。
/// </summary>
[DisallowMultipleComponent]
public class EnemyVisibility : MonoBehaviour
{
    public Flashlight2D flashlight;

    private SpriteRenderer spriteRenderer;

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    private void LateUpdate()
    {
        if (spriteRenderer == null)
            return;

        if (flashlight == null)
        {
            spriteRenderer.enabled = true;
            return;
        }

        spriteRenderer.enabled = flashlight.IsIlluminating(transform.position);
    }
}
