using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 路线标记：按 Q（可在 Inspector 改键）时，在玩家当前位置放下一枚叉号。
/// 用来记录已经走过的路线，帮助玩家在黑暗里辨认方向。
///
/// 黑暗中为较深的暗绿色，只能隐约看见；被手电照到时变成与玩家一致的亮绿色，
/// 形成“用光照亮路标”的效果。
///
/// 贴图完全由代码生成，不依赖任何美术资源；材质使用无光照 Sprite，
/// 颜色由脚本根据手电照明实时切换。标记挂在场景根节点下，不会跟着玩家移动。
/// 本组件挂在 Player 上，不依赖也不修改其他玩法脚本。
/// </summary>
[DisallowMultipleComponent]
public class TrailMarker : MonoBehaviour
{
    [Header("按键 / Key")]
    [Tooltip("放置标记的按键，默认 Q。为 None 时也会回退到 Q。")]
    [SerializeField] private Key markKey = Key.Q;

    [Header("标记外观 / Appearance")]
    [Tooltip("叉号的世界尺寸（Unity 世界单位）。")]
    [Min(0.05f)]
    [SerializeField] private float markSize = 0.55f;

    [Tooltip("黑暗中的颜色：较深的暗绿色，仅隐约可见。")]
    [SerializeField] private Color darkColor = new Color(0.04f, 0.14f, 0.07f, 1f);

    [Tooltip("被手电照到时的颜色：默认与玩家的亮绿色一致。")]
    [SerializeField] private Color litColor = new Color(0f, 1f, 0f, 1f);

    [Tooltip("从暗到亮的过渡速度，数值越大越接近瞬间亮起；0 表示立即切换。")]
    [Min(0f)]
    [SerializeField] private float revealSpeed = 20f;

    [Tooltip("线条粗细，相对标记尺寸的比例。")]
    [Range(0.05f, 0.4f)]
    [SerializeField] private float lineThickness = 0.16f;

    [Tooltip("相对玩家的位置偏移；默认放在玩家脚下。")]
    [SerializeField] private Vector2 positionOffset = Vector2.zero;

    [Header("光照检测 / Lighting")]
    [Tooltip("玩家手电；留空时自动从子物体或场景中寻找。")]
    [SerializeField] private Flashlight2D flashlight;

    [Header("排序 / Sorting")]
    [Tooltip("排序层名称，留空则不改动。")]
    [SerializeField] private string sortingLayerName = "Default";

    [Tooltip("排序序号：地板是 -10、玩家是 0、墙是 10；默认 -5 位于地板之上、玩家和墙之下。")]
    [SerializeField] private int sortingOrder = -5;

    [Header("放置规则 / Rules")]
    [Tooltip("两次放置之间的冷却时间（秒），默认 2 秒。")]
    [Min(0f)]
    [SerializeField] private float placeCooldown = 2f;

    [Tooltip("与上一枚标记的最小距离，避免原地连按堆叠。")]
    [Min(0f)]
    [SerializeField] private float minDistance = 0.25f;

    [Tooltip("最多保留的标记数量，超出后删除最早的；0 表示不限制。")]
    [Min(0)]
    [SerializeField] private int maxMarks = 300;

    private const int TextureSize = 64;

    // 光照检测不需要每帧做物理射线，0.05 秒的间隔足够跟上手电的移动。
    private const float LightCheckInterval = 0.05f;

    private sealed class Mark
    {
        public GameObject gameObject;
        public SpriteRenderer renderer;
        public Vector2 position;
        public bool illuminated;
    }

    // 已生成的标记，按放置顺序保存，便于限制数量时删除最早的。
    private readonly List<Mark> marks = new List<Mark>();

    private Transform markRoot;
    private Sprite markSprite;
    private Texture2D markTexture;
    private Material markMaterial;

    private Vector2 lastMarkPosition;
    private bool hasLastMark;
    private float nextPlaceTime;
    private float nextLightCheckTime;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponentInChildren<Flashlight2D>(true);
        if (flashlight == null)
            flashlight = FindAnyObjectByType<Flashlight2D>();

        CreateSprite();
        CreateMaterial();
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !Application.isFocused)
            return;

        // 默认 Q；即使字段被序列化成 None，也仍然保证 Q 可用。
        Key key = markKey == Key.None ? Key.Q : markKey;
        if (!keyboard[key].wasPressedThisFrame)
            return;

        // 冷却时间内按下 Q 不生效。
        if (Time.time < nextPlaceTime)
            return;

        PlaceMark();
    }

    private void LateUpdate()
    {
        UpdateMarkBrightness();
    }

    private void PlaceMark()
    {
        Vector2 position = (Vector2)transform.position + positionOffset;

        // 原地连按不重复堆叠；玩家移动一段距离后才会放下新标记。
        if (hasLastMark && Vector2.Distance(position, lastMarkPosition) < Mathf.Max(0f, minDistance))
            return;

        if (markSprite == null)
            CreateSprite();
        if (markMaterial == null)
            CreateMaterial();

        GameObject markObject = new GameObject("TrailMark");
        markObject.transform.SetParent(GetMarkRoot(), false);
        markObject.transform.position = new Vector3(position.x, position.y, 0f);
        // 轻微随机角度，让一连串叉号看起来更像手绘路标。
        markObject.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(-8f, 8f));
        markObject.transform.localScale = Vector3.one * Mathf.Max(0.05f, markSize);

        SpriteRenderer renderer = markObject.AddComponent<SpriteRenderer>();
        renderer.sprite = markSprite;
        renderer.color = darkColor;
        if (!string.IsNullOrEmpty(sortingLayerName))
            renderer.sortingLayerName = sortingLayerName;
        renderer.sortingOrder = sortingOrder;
        if (markMaterial != null)
            renderer.sharedMaterial = markMaterial;

        marks.Add(new Mark
        {
            gameObject = markObject,
            renderer = renderer,
            position = position,
            illuminated = false
        });

        lastMarkPosition = position;
        hasLastMark = true;
        // 只有真正放下标记才消耗冷却；因距离太近被忽略的按键不占用冷却。
        nextPlaceTime = Time.time + Mathf.Max(0f, placeCooldown);

        TrimMarks();
    }

    // 标记放在独立根节点下，不跟随玩家移动；场景切换时随场景一起销毁。
    private Transform GetMarkRoot()
    {
        if (markRoot == null)
        {
            GameObject root = new GameObject("TrailMarks");
            markRoot = root.transform;
        }

        return markRoot;
    }

    private void TrimMarks()
    {
        if (maxMarks <= 0)
            return;

        while (marks.Count > maxMarks)
        {
            Mark oldest = marks[0];
            marks.RemoveAt(0);

            if (oldest.gameObject != null)
                Destroy(oldest.gameObject);
        }
    }

    // 只有被手电真正照到（在扇形内、距离内、且无墙遮挡）的标记才变亮。
    private void UpdateMarkBrightness()
    {
        bool checkLight = Time.time >= nextLightCheckTime;
        if (checkLight)
            nextLightCheckTime = Time.time + LightCheckInterval;

        bool hasFlashlight = flashlight != null && flashlight.isActiveAndEnabled;
        float rangeSquared = 0f;
        Vector2 lightPosition = Vector2.zero;

        if (hasFlashlight)
        {
            float range = Mathf.Max(0f, flashlight.range);
            rangeSquared = range * range;
            lightPosition = flashlight.transform.position;
        }

        float t = revealSpeed > 0f
            ? 1f - Mathf.Exp(-revealSpeed * Time.deltaTime)
            : 1f;

        for (int i = 0; i < marks.Count; i++)
        {
            Mark mark = marks[i];
            if (mark == null || mark.renderer == null)
                continue;

            if (checkLight)
            {
                bool illuminated = false;

                // 先按距离快速排除，避免为每个标记都做物理射线检测。
                if (hasFlashlight &&
                    (mark.position - lightPosition).sqrMagnitude <= rangeSquared)
                {
                    illuminated = flashlight.IsIlluminating(mark.position);
                }

                mark.illuminated = illuminated;
            }

            Color target = mark.illuminated ? litColor : darkColor;
            mark.renderer.color = t >= 1f
                ? target
                : Color.Lerp(mark.renderer.color, target, t);
        }
    }

    // 生成一张带 1 像素抗锯齿的白色叉号贴图，颜色由 SpriteRenderer.color 决定。
    private void CreateSprite()
    {
        markTexture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
        markTexture.name = "TrailMarkerTexture";
        markTexture.hideFlags = HideFlags.HideAndDontSave;
        markTexture.wrapMode = TextureWrapMode.Clamp;
        markTexture.filterMode = FilterMode.Bilinear;

        float padding = TextureSize * 0.16f;
        float thickness = Mathf.Max(1.5f, TextureSize * Mathf.Clamp(lineThickness, 0.05f, 0.4f));
        float halfThickness = thickness * 0.5f;

        Vector2 bottomLeft = new Vector2(padding, padding);
        Vector2 topLeft = new Vector2(padding, TextureSize - padding);
        Vector2 bottomRight = new Vector2(TextureSize - padding, padding);
        Vector2 topRight = new Vector2(TextureSize - padding, TextureSize - padding);

        Color32[] pixels = new Color32[TextureSize * TextureSize];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                Vector2 point = new Vector2(x + 0.5f, y + 0.5f);
                float distance = Mathf.Min(
                    DistanceToSegment(point, bottomLeft, topRight),
                    DistanceToSegment(point, topLeft, bottomRight));

                // 距离场边缘做 1 像素过渡，避免锯齿。
                float alpha = Mathf.Clamp01(halfThickness + 0.5f - distance);
                pixels[y * TextureSize + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        markTexture.SetPixels32(pixels);
        markTexture.Apply(false, false);

        markSprite = Sprite.Create(
            markTexture,
            new Rect(0f, 0f, TextureSize, TextureSize),
            new Vector2(0.5f, 0.5f),
            TextureSize);
        markSprite.name = "TrailMarkerSprite";
        markSprite.hideFlags = HideFlags.HideAndDontSave;
    }

    private void CreateMaterial()
    {
        // 无光照 Sprite：颜色完全由脚本控制，便于在暗绿与亮绿之间切换。
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        if (shader == null)
            return;

        markMaterial = new Material(shader);
        markMaterial.name = "TrailMarkerUnlit";
        markMaterial.hideFlags = HideFlags.HideAndDontSave;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.sqrMagnitude;
        if (lengthSquared < 0.000001f)
            return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
        return Vector2.Distance(point, a + ab * t);
    }

    private void OnDestroy()
    {
        if (markRoot != null)
            Destroy(markRoot.gameObject);

        marks.Clear();

        if (markMaterial != null)
            Destroy(markMaterial);

        if (markSprite != null)
            Destroy(markSprite);

        if (markTexture != null)
            Destroy(markTexture);
    }
}
