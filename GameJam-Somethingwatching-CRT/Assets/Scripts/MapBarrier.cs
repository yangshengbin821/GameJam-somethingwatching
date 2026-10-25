using UnityEngine;

/// <summary>
/// 地图上的可拆障碍。被 ObstacleInteraction 读完条后销毁。
/// 后墙由 MapBuilder 在生成地图时预先生成并紧贴障碍；
/// 这里只负责在拆除后把障碍原占据的像素区域铺成地板色，让玩家可以走进凹位。
/// </summary>
public class MapBarrier : MonoBehaviour
{
    public Texture2D mapTexture;
    public RectInt pixelRect;

    [Tooltip("障碍拆除后原地留下的地板颜色。")]
    public Color fillColor = new Color(0.925f, 0.925f, 0.925f, 1f);

    private void OnDestroy()
    {
        if (mapTexture == null)
            return;

        if (!mapTexture.isReadable)
        {
            Debug.LogWarning("MapBarrier: 地图贴图没有开启 Read/Write，无法擦除双横线。", this);
            return;
        }

        FillRect(pixelRect, fillColor);
    }

    private void FillRect(RectInt rect, Color32 color)
    {
        int x = Mathf.Clamp(rect.x, 0, mapTexture.width - 1);
        int y = Mathf.Clamp(rect.y, 0, mapTexture.height - 1);
        int width = Mathf.Clamp(rect.width, 1, mapTexture.width - x);
        int height = Mathf.Clamp(rect.height, 1, mapTexture.height - y);

        // 数据里的 y 以图片左上角为原点；Unity 贴图以左下角为原点。
        int textureY = mapTexture.height - (y + height);
        if (textureY < 0)
        {
            height += textureY;
            textureY = 0;
        }
        if (width <= 0 || height <= 0)
            return;

        Color32[] pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;

        mapTexture.SetPixels32(x, textureY, width, height, pixels);
        mapTexture.Apply(false, false);
    }
}
