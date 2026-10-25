using System;
using UnityEngine;

/// <summary>
/// 根据 Assets/Map/MapData.json 在运行时搭建地图：
/// - 用「非地板区域」的合并矩形生成静态墙体碰撞（地板就是可走区域）；
/// - 在双横线位置生成带 RemovableObstacle 标记的可拆障碍；
/// - 放置玩家、怪物和终点触发器。
/// 地图视觉由同场景的 MapVisual SpriteRenderer 提供。
/// </summary>
public class MapBuilder : MonoBehaviour
{
    [Header("引用 / References")]
    [SerializeField] private SpriteRenderer mapRenderer;
    [SerializeField] private Transform player;
    [SerializeField] private Transform enemy;
    [SerializeField] private TextAsset mapDataAsset;

    private float pixelsPerUnit = 44.44f;
    private float halfWidth;
    private float halfHeight;

    private Texture2D mapTexture;
    private Texture2D whiteTexture;
    private Sprite whiteSprite;
    private Material wallMaterial;

    private void Awake()
    {
        if (mapDataAsset == null)
        {
            Debug.LogError("MapBuilder: 没有指定 MapData.json。");
            return;
        }

        MapLevelData data = JsonUtility.FromJson<MapLevelData>(mapDataAsset.text);
        if (data == null || data.textureWidth <= 0 || data.textureHeight <= 0)
        {
            Debug.LogError("MapBuilder: MapData.json 解析失败。");
            return;
        }

        pixelsPerUnit = data.pixelsPerUnit > 0f ? data.pixelsPerUnit : 44.44f;
        halfWidth = data.textureWidth * 0.5f;
        halfHeight = data.textureHeight * 0.5f;

        if (mapRenderer != null)
        {
            if (mapRenderer.sprite != null)
                mapTexture = mapRenderer.sprite.texture;
            wallMaterial = mapRenderer.sharedMaterial;
        }

        BuildWalls(data);
        PreGenerateBackWalls(data);
        BuildBarriers(data);
        PlaceActors(data);
        BuildGoal(data);
    }

    private Vector2 PixelToWorld(float px, float py)
    {
        return new Vector2(
            (px - halfWidth) / pixelsPerUnit,
            (halfHeight - py) / pixelsPerUnit);
    }

    private void BuildWalls(MapLevelData data)
    {
        if (data.solids == null || data.solids.Length == 0)
            return;

        GameObject walls = new GameObject("MapWalls");
        walls.transform.SetParent(transform, false);

        foreach (MapRectData rect in data.solids)
        {
            if (rect.w <= 0 || rect.h <= 0)
                continue;

            GameObject wall = new GameObject("Wall");
            wall.layer = 3; // Obstacle
            wall.transform.SetParent(walls.transform, false);
            wall.transform.position = PixelToWorld(
                rect.x + rect.w * 0.5f,
                rect.y + rect.h * 0.5f);
            wall.transform.localScale = new Vector3(
                rect.w / pixelsPerUnit,
                rect.h / pixelsPerUnit,
                1f);

            SpriteRenderer renderer = wall.AddComponent<SpriteRenderer>();
            renderer.sprite = GetWhiteSprite();
            renderer.color = new Color32(50, 50, 50, 255);
            renderer.sortingOrder = 0;
            if (wallMaterial != null)
                renderer.sharedMaterial = wallMaterial;

            BoxCollider2D box = wall.AddComponent<BoxCollider2D>();
            box.size = Vector2.one;
        }
    }

    private void PreGenerateBackWalls(MapLevelData data)
    {
        if (data.barriers == null || data.barriers.Length == 0)
            return;

        GameObject root = new GameObject("PreGeneratedBackWalls");
        root.transform.SetParent(transform, false);

        foreach (MapBarrierData barrier in data.barriers)
        {
            if (barrier.backW <= 0 || barrier.backH <= 0)
                continue;

            if (BarrierContainsPoint(barrier, data.start))
                continue;

            // 根据障碍矩形重新吸附到边缘，并额外重叠 1 像素，避免缝隙。
            RectInt backRect = GetTightBackRect(barrier);

            GameObject wall = new GameObject("BackWall");
            wall.layer = 3; // Obstacle
            wall.transform.SetParent(root.transform, false);
            wall.transform.position = PixelToWorld(
                backRect.x + backRect.width * 0.5f,
                backRect.y + backRect.height * 0.5f);
            wall.transform.localScale = new Vector3(
                backRect.width / pixelsPerUnit,
                backRect.height / pixelsPerUnit,
                1f);

            SpriteRenderer renderer = wall.AddComponent<SpriteRenderer>();
            renderer.sprite = GetWhiteSprite();
            renderer.color = new Color32(50, 50, 50, 255);
            renderer.sortingOrder = 0;
            if (wallMaterial != null)
                renderer.sharedMaterial = wallMaterial;

            BoxCollider2D collider = wall.AddComponent<BoxCollider2D>();
            collider.size = Vector2.one;
        }
    }

    private RectInt GetTightBackRect(MapBarrierData barrier)
    {
        if (barrier.w >= barrier.h)
        {
            // 横墙：后墙在障碍上侧或下侧。
            int thickness = Mathf.Max(1, barrier.backH);
            int barrierCenter = barrier.y + barrier.h / 2;
            int backCenter = barrier.backY + barrier.backH / 2;
            int y = backCenter >= barrierCenter
                ? barrier.y + barrier.h - 1
                : barrier.y - thickness + 1;
            return new RectInt(barrier.x, y, barrier.w, thickness);
        }
        else
        {
            // 竖墙：后墙在障碍左侧或右侧。
            int thickness = Mathf.Max(1, barrier.backW);
            int barrierCenter = barrier.x + barrier.w / 2;
            int backCenter = barrier.backX + barrier.backW / 2;
            int x = backCenter >= barrierCenter
                ? barrier.x + barrier.w - 1
                : barrier.x - thickness + 1;
            return new RectInt(x, barrier.y, thickness, barrier.h);
        }
    }

    private void PaintMapRect(RectInt rect, Color32 color)
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
    }

    private bool BarrierContainsPoint(MapBarrierData barrier, int[] point)
    {
        return point != null && point.Length >= 2 &&
            point[0] >= barrier.x && point[0] <= barrier.x + barrier.w &&
            point[1] >= barrier.y && point[1] <= barrier.y + barrier.h;
    }

    private void BuildBarriers(MapLevelData data)
    {
        if (data.barriers == null || data.barriers.Length == 0)
            return;

        GameObject root = new GameObject("MapBarriers");
        root.transform.SetParent(transform, false);

        for (int i = 0; i < data.barriers.Length; i++)
        {
            MapBarrierData barrier = data.barriers[i];
            if (barrier.w <= 0 || barrier.h <= 0)
                continue;

            // 起点/门口这条障碍不生成，避免玩家出生在障碍上。
            if (BarrierContainsPoint(barrier, data.start))
                continue;

            GameObject go = new GameObject("Barrier_" + i);
            go.layer = 3; // Obstacle
            go.transform.SetParent(root.transform, false);
            go.transform.position = PixelToWorld(
                barrier.x + barrier.w * 0.5f,
                barrier.y + barrier.h * 0.5f);

            BoxCollider2D collider = go.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(
                barrier.w / pixelsPerUnit,
                barrier.h / pixelsPerUnit);

            go.AddComponent<RemovableObstacle>();

            // 障碍物自己的可见外观（拆除时随障碍物体一起销毁）。
            GameObject visual = new GameObject("Visual");
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = new Vector3(
                barrier.w / pixelsPerUnit,
                barrier.h / pixelsPerUnit,
                1f);
            SpriteRenderer visualRenderer = visual.AddComponent<SpriteRenderer>();
            visualRenderer.sprite = GetWhiteSprite();
            visualRenderer.color = new Color(0.64f, 0.38f, 0.16f, 1f);
            visualRenderer.sortingOrder = 1;
            if (wallMaterial != null)
                visualRenderer.sharedMaterial = wallMaterial;

            // 平时 alpha = 0 不可见；被 ObstacleInteraction 选中时 Lerp 到白色，形成高亮。
            GameObject highlight = new GameObject("Highlight");
            highlight.transform.SetParent(go.transform, false);
            SpriteRenderer renderer = highlight.AddComponent<SpriteRenderer>();
            renderer.sprite = GetWhiteSprite();
            renderer.color = new Color(1f, 1f, 1f, 0f);
            renderer.sortingOrder = 5;
            highlight.transform.localScale = new Vector3(
                barrier.w / pixelsPerUnit,
                barrier.h / pixelsPerUnit,
                1f);
        }
    }

    private void PlaceActors(MapLevelData data)
    {
        if (player != null && data.start != null && data.start.Length >= 2)
        {
            player.position = PixelToWorld(data.start[0], data.start[1]);
            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            if (body != null)
                body.position = player.position;
        }

        // 怪物原本是 Player 的子物体，移到根节点，避免被玩家移动/旋转带着走。
        if (enemy != null && data.enemy != null && data.enemy.Length >= 2)
        {
            enemy.SetParent(null, true);
            enemy.position = PixelToWorld(data.enemy[0], data.enemy[1]);
            Rigidbody2D body = enemy.GetComponent<Rigidbody2D>();
            if (body != null)
                body.position = enemy.position;

            // 地图墙体没有自动阴影时，怪物只在实际被手电照到时显示，避免隔墙看见。
            EnemyChase chase = enemy.GetComponent<EnemyChase>();
            if (chase != null && chase.flashlight != null &&
                enemy.GetComponent<EnemyVisibility>() == null)
            {
                EnemyVisibility visibility = enemy.gameObject.AddComponent<EnemyVisibility>();
                visibility.flashlight = chase.flashlight;
            }
        }
    }

    private void BuildGoal(MapLevelData data)
    {
        if (data.goalRect == null || data.goalRect.Length < 4)
            return;

        GameObject goal = new GameObject("LevelGoal");
        goal.transform.SetParent(transform, false);

        float centerX = data.goalRect[0] + data.goalRect[2] * 0.5f;
        float centerY = data.goalRect[1] + data.goalRect[3] * 0.5f;
        goal.transform.position = PixelToWorld(centerX, centerY);

        BoxCollider2D trigger = goal.AddComponent<BoxCollider2D>();
        trigger.isTrigger = true;
        trigger.size = new Vector2(
            data.goalRect[2] / pixelsPerUnit,
            data.goalRect[3] / pixelsPerUnit);

        GameObject marker = new GameObject("GoalMarker");
        marker.transform.SetParent(goal.transform, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localScale = new Vector3(
            data.goalRect[2] / pixelsPerUnit,
            data.goalRect[3] / pixelsPerUnit,
            1f);
        SpriteRenderer renderer = marker.AddComponent<SpriteRenderer>();
        renderer.sprite = GetWhiteSprite();
        renderer.color = new Color(0.95f, 0.8f, 0.15f, 0.9f);
        renderer.sortingOrder = 1;
        if (wallMaterial != null)
            renderer.sharedMaterial = wallMaterial;

        goal.AddComponent<LevelGoal>();
    }

    private Sprite GetWhiteSprite()
    {
        if (whiteSprite != null)
            return whiteSprite;

        whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        whiteTexture.name = "MapBuilderWhite";
        whiteTexture.hideFlags = HideFlags.HideAndDontSave;
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply();

        whiteSprite = Sprite.Create(
            whiteTexture,
            new Rect(0f, 0f, 1f, 1f),
            new Vector2(0.5f, 0.5f),
            1f);
        whiteSprite.name = "MapBuilderWhiteSprite";
        whiteSprite.hideFlags = HideFlags.HideAndDontSave;
        return whiteSprite;
    }

    private void OnDestroy()
    {
        if (whiteSprite != null)
            Destroy(whiteSprite);

        if (whiteTexture != null)
            Destroy(whiteTexture);
    }
}

[Serializable]
public class MapLevelData
{
    public float pixelsPerUnit;
    public int textureWidth;
    public int textureHeight;
    public MapRectData[] solids;
    public MapBarrierData[] barriers;
    public int[] start;
    public int[] goal;
    public int[] goalRect;
    public int[] enemy;
}

[Serializable]
public class MapRectData
{
    public int x;
    public int y;
    public int w;
    public int h;
}

[Serializable]
public class MapBarrierData
{
    public int x;
    public int y;
    public int w;
    public int h;
    public bool boundary;
    public int backX;
    public int backY;
    public int backW;
    public int backH;
}
