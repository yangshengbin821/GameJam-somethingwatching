using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 一键把 MapData.json 中的墙、可拆窗、地板和终点烘焙成场景对象，
/// 之后关闭 MapBuilder，不再运行时生成。
/// 菜单：Tools > Bake Map (Walls + Windows)
/// </summary>
public static class MapBaker
{
    private static void AddShadowCaster(GameObject go)
    {
        ShadowCaster2D caster = go.AddComponent<ShadowCaster2D>();
        var property = typeof(ShadowCaster2D).GetProperty("useRendererSilhouette");
        if (property != null)
            property.SetValue(caster, true);
    }

    [MenuItem("Tools/Bake Map (Walls + Windows)")]
    public static void Bake()
    {
        MapBuilder builder = Object.FindAnyObjectByType<MapBuilder>();
        if (builder == null)
        {
            Debug.LogError("MapBaker: 当前场景里没有 MapBuilder。");
            return;
        }

        const string dataPath = "Assets/Map/MapData.json";
        if (!File.Exists(dataPath))
        {
            Debug.LogError("MapBaker: 找不到 " + dataPath);
            return;
        }

        MapLevelData data = JsonUtility.FromJson<MapLevelData>(File.ReadAllText(dataPath));
        Sprite white = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Map/White.png");
        if (data == null || white == null)
        {
            Debug.LogError("MapBaker: MapData.json 或 White.png 无法读取。");
            return;
        }

        GameObject old = GameObject.Find("PreplacedMap");
        if (old != null)
            Object.DestroyImmediate(old);

        GameObject root = new GameObject("PreplacedMap");
        Undo.RegisterCreatedObjectUndo(root, "Bake Map");
        builder.enabled = false;

        float ppu = data.pixelsPerUnit;
        float halfW = data.textureWidth * 0.5f;
        float halfH = data.textureHeight * 0.5f;
        Vector2 ToWorld(float x, float y) => new Vector2((x - halfW) / ppu, (halfH - y) / ppu);

        // 地板：复用 MapVisual，不再使用图片，只保留纯灰 Sprite。
        SpriteRenderer floor = GameObject.Find("MapVisual")?.GetComponent<SpriteRenderer>();
        if (floor != null)
        {
            floor.sprite = white;
            floor.color = new Color32(236, 236, 236, 255);
            floor.sortingOrder = -10;
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(
                data.textureWidth / ppu,
                data.textureHeight / ppu,
                1f);
        }
        Material mat = floor != null ? floor.sharedMaterial : null;

        // 墙体
        if (data.walls != null)
        {
            for (int i = 0; i < data.walls.Length; i++)
            {
                MapWallData w = data.walls[i];
                GameObject go = new GameObject("Wall_" + i);
                go.layer = 3; // Obstacle
                go.transform.SetParent(root.transform, false);
                go.transform.position = ToWorld(w.x, w.y);
                go.transform.rotation = Quaternion.Euler(0f, 0f, w.angle);
                go.transform.localScale = new Vector3(w.w / ppu, w.h / ppu, 1f);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = white;
                sr.color = new Color32(50, 50, 50, 255);
                sr.sortingOrder = 0;
                if (mat != null) sr.sharedMaterial = mat;

                BoxCollider2D box = go.AddComponent<BoxCollider2D>();
                box.size = Vector2.one;
                AddShadowCaster(go);
            }
        }

        // 可拆窗
        if (data.barriers != null)
        {
            for (int i = 0; i < data.barriers.Length; i++)
            {
                MapBarrierData b = data.barriers[i];
                GameObject go = new GameObject("Barrier_" + i);
                go.layer = 3; // Obstacle
                go.transform.SetParent(root.transform, false);
                go.transform.position = ToWorld(b.x, b.y);
                go.transform.localScale = new Vector3(b.w / ppu, b.h / ppu, 1f);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = white;
                sr.color = new Color(0.64f, 0.38f, 0.16f, 1f);
                sr.sortingOrder = 1;
                if (mat != null) sr.sharedMaterial = mat;

                BoxCollider2D box = go.AddComponent<BoxCollider2D>();
                box.size = Vector2.one;
                AddShadowCaster(go);

                go.AddComponent<RemovableObstacle>();

                GameObject hl = new GameObject("Highlight");
                hl.transform.SetParent(go.transform, false);
                SpriteRenderer hsr = hl.AddComponent<SpriteRenderer>();
                hsr.sprite = white;
                hsr.color = new Color(1f, 1f, 1f, 0f);
                hsr.sortingOrder = 5;
                if (mat != null) hsr.sharedMaterial = mat;
                hl.transform.localScale = Vector3.one;
            }
        }

        // 终点
        if (data.goalRect != null && data.goalRect.Length >= 4)
        {
            GameObject go = new GameObject("LevelGoal");
            go.transform.SetParent(root.transform, false);
            float cx = data.goalRect[0] + data.goalRect[2] * 0.5f;
            float cy = data.goalRect[1] + data.goalRect[3] * 0.5f;
            go.transform.position = ToWorld(cx, cy);

            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(
                data.goalRect[2] / ppu,
                data.goalRect[3] / ppu);

            go.AddComponent<LevelGoal>();

            GameObject marker = new GameObject("GoalMarker");
            marker.transform.SetParent(go.transform, false);
            marker.transform.localScale = new Vector3(
                data.goalRect[2] / ppu,
                data.goalRect[3] / ppu,
                1f);
            SpriteRenderer msr = marker.AddComponent<SpriteRenderer>();
            msr.sprite = white;
            msr.color = new Color(0.95f, 0.8f, 0.15f, 0.9f);
            msr.sortingOrder = 1;
            if (mat != null) msr.sharedMaterial = mat;
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        int wallCount = data.walls != null ? data.walls.Length : 0;
        int barrierCount = data.barriers != null ? data.barriers.Length : 0;
        Debug.Log($"MapBaker: 烘焙完成，{wallCount} 段墙，{barrierCount} 个可拆窗。保存场景后生效。");
    }
}
