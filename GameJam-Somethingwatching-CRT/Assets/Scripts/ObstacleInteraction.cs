using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 面朝方向（角色与鼠标指针的连线）一定距离内、挂有 RemovableObstacle 标记的障碍物会被略微高亮。
/// 按 E 出现进度条；读条期间若不再满足方向 / 距离 / 不被墙遮挡的条件，
/// 进度暂停但进度条保留；读条完成后该障碍物消失。
///
/// 只取射线命中的第一个障碍物，因此不可能隔着墙操作；没有标记组件的墙会挡住后面的可拆目标。
/// 本组件应挂在独立的空物体上，不依赖也不修改原有玩法脚本。
/// </summary>
public class ObstacleInteraction : MonoBehaviour
{
    [Header("引用 / References")]
    [Tooltip("角色；留空时自动寻找场景中的 PlayerMovement。")]
    [SerializeField] private Transform player;

    [Tooltip("用于把鼠标位置转换到世界的相机；留空时使用 Camera.main。")]
    [SerializeField] private Camera aimCamera;

    [Header("选取 / Targeting")]
    [Tooltip("被视为障碍物的层。场景里的墙在 Obstacle 层（第 3 层）。")]
    [SerializeField] private LayerMask obstacleLayers;

    [Tooltip("角色到鼠标方向上的最大交互距离（只对最近的障碍物生效）。")]
    [Min(0.1f)]
    [SerializeField] private float interactRange = 1f;

    [Tooltip("选中障碍物时朝白色混合的强度，0 表示不高亮。")]
    [Range(0f, 1f)]
    [SerializeField] private float highlightStrength = 0.3f;

    [Header("读条 / Reading")]
    [Tooltip("读条完成所需时间（秒）。")]
    [Min(0.05f)]
    [SerializeField] private float readDuration = 2f;

    [Header("进度条 / Progress Bar")]
    [SerializeField] private float barWidth = 0.9f;
    [SerializeField] private float barHeight = 0.12f;

    [Tooltip("进度条相对瞄准命中点向上偏移的世界距离。")]
    [SerializeField] private float barOffset = 0.45f;

    [SerializeField] private Color barActiveColor = new Color(0.25f, 1f, 0.45f, 1f);
    [SerializeField] private Color barPausedColor = new Color(1f, 0.8f, 0.25f, 1f);
    [SerializeField] private int barSortingOrder = 100;

    // 当前被高亮的物体及其原始颜色。
    private GameObject highlightedTarget;
    private readonly List<SpriteRenderer> highlightRenderers = new List<SpriteRenderer>();
    private readonly List<Color> highlightColors = new List<Color>();

    // 读条状态。
    private GameObject readTarget;
    private float readProgress;
    private bool readPaused;
    private Vector2 barPosition;

    // 运行时生成的进度条。
    private Transform barRoot;
    private Transform barFill;
    private SpriteRenderer barFillRenderer;
    private Texture2D barTexture;
    private Sprite barBackgroundSprite;
    private Sprite barFillSprite;

    private Vector2 aimDirection = Vector2.up;

    // 避免每帧分配：射线可能同时命中多个障碍物。
    private readonly RaycastHit2D[] obstacleHits = new RaycastHit2D[16];

    private void Awake()
    {
        if (player == null)
        {
            PlayerMovement movement = FindAnyObjectByType<PlayerMovement>();
            if (movement != null)
                player = movement.transform;
        }

        if (aimCamera == null)
            aimCamera = Camera.main;

        if (obstacleLayers.value == 0)
            obstacleLayers = LayerMask.GetMask("Obstacle");

        CreateBarSprites();
    }

    private void Update()
    {
        if (player == null)
            return;

        UpdateAimDirection();

        RaycastHit2D hit = FindFirstObstacle();
        GameObject selected = FindRemovableTarget(hit);

        UpdateHighlight(selected);

        // 按 E：开始读条；若正读着另一个物体，则放弃旧读条、改读当前物体。
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame && selected != null)
        {
            if (readTarget != null && readTarget != selected)
                CancelRead();

            if (readTarget == null)
                BeginRead(selected, hit);
        }

        UpdateRead(selected, hit);
    }

    // 用与 PlayerMovement 相同的方式，把鼠标位置投到角色所在的 XY 平面。
    private void UpdateAimDirection()
    {
        if (aimCamera == null || Mouse.current == null)
        {
            Vector2 fallback = player.up;
            if (fallback.sqrMagnitude > 0.0001f)
                aimDirection = fallback.normalized;
            return;
        }

        Vector2 screenPosition = Mouse.current.position.ReadValue();
        Ray ray = aimCamera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));
        Plane plane = new Plane(Vector3.forward, player.position);

        if (plane.Raycast(ray, out float distance))
        {
            Vector2 direction = (Vector2)ray.GetPoint(distance) - (Vector2)player.position;
            if (direction.sqrMagnitude > 0.0001f)
                aimDirection = direction.normalized;
        }
    }

    // 只返回射线上最近的非 Trigger 障碍物：更近的墙会先被命中，
    // 因此目标被墙挡住时永远不会被选中，也无法隔墙读条；
    // 没有 RemovableObstacle 标记的墙同样会先挡住后面的可拆目标。
    private RaycastHit2D FindFirstObstacle()
    {
        int count = Physics2D.RaycastNonAlloc(
            player.position,
            aimDirection,
            obstacleHits,
            Mathf.Max(0.1f, interactRange),
            obstacleLayers);

        RaycastHit2D nearest = default;
        float nearestDistance = float.PositiveInfinity;

        for (int i = 0; i < count; i++)
        {
            RaycastHit2D hit = obstacleHits[i];
            if (hit.collider == null || hit.collider.isTrigger)
                continue;
            if (hit.distance >= nearestDistance)
                continue;

            nearest = hit;
            nearestDistance = hit.distance;
        }

        return nearest;
    }

    // 只有挂了 RemovableObstacle 的障碍物才是可交互目标。
    // 标记可以挂在碰撞体所在的物体或它的父物体上；原始测试墙没有标记，因此保持不可拆。
    private GameObject FindRemovableTarget(RaycastHit2D hit)
    {
        if (hit.collider == null)
            return null;

        RemovableObstacle removable = hit.collider.GetComponentInParent<RemovableObstacle>();
        return removable != null ? removable.gameObject : null;
    }

    private void UpdateHighlight(GameObject target)
    {
        if (highlightedTarget == target)
            return;

        RestoreHighlight();
        highlightedTarget = target;

        if (target == null)
            return;

        foreach (SpriteRenderer spriteRenderer in target.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (spriteRenderer == null)
                continue;

            highlightRenderers.Add(spriteRenderer);
            highlightColors.Add(spriteRenderer.color);
            spriteRenderer.color = Color.Lerp(spriteRenderer.color, Color.white, highlightStrength);
        }
    }

    private void RestoreHighlight()
    {
        for (int i = 0; i < highlightRenderers.Count; i++)
        {
            if (highlightRenderers[i] != null)
                highlightRenderers[i].color = highlightColors[i];
        }

        highlightRenderers.Clear();
        highlightColors.Clear();
        highlightedTarget = null;
    }

    private void BeginRead(GameObject target, RaycastHit2D hit)
    {
        readTarget = target;
        readProgress = 0f;
        readPaused = false;
        barPosition = GetBarPosition(hit);

        EnsureBar();
        UpdateBarVisual();
        UpdateBarTransform();
    }

    private void UpdateRead(GameObject selected, RaycastHit2D hit)
    {
        // 目标被其他逻辑销毁时，清掉残留进度条。
        if (readTarget == null)
        {
            if (barRoot != null)
                CancelRead();
            return;
        }

        // 条件：读条目标仍是鼠标射线第一个障碍物上解析出的可拆标记物体。
        // 这同时覆盖了“在方向上 / 在距离内 / 未被墙遮挡”三点；
        // 如果前方换成了一面没有标记的墙，selected 会变成 null，读条暂停。
        bool conditionMet = selected == readTarget && hit.collider != null;

        if (conditionMet)
        {
            readPaused = false;
            barPosition = GetBarPosition(hit);
            readProgress += Time.deltaTime;

            if (readProgress >= Mathf.Max(0.05f, readDuration))
            {
                CompleteRead();
                return;
            }
        }
        else
        {
            // 不满足条件时只暂停，不清空进度，进度条保持可见。
            readPaused = true;
        }

        UpdateBarVisual();
        UpdateBarTransform();
    }

    private void CompleteRead()
    {
        GameObject finished = readTarget;

        CancelRead();

        // 目标即将被移除，不需要把颜色改回去；这里只是避免留下失效引用。
        if (highlightedTarget == finished)
            RestoreHighlight();

        if (finished != null)
            Destroy(finished);
    }

    private void CancelRead()
    {
        if (barRoot != null)
        {
            Destroy(barRoot.gameObject);
            barRoot = null;
            barFill = null;
            barFillRenderer = null;
        }

        readTarget = null;
        readProgress = 0f;
        readPaused = false;
    }

    private Vector2 GetBarPosition(RaycastHit2D hit)
    {
        // 命中点永远在屏幕内；让进度条浮在命中点前上方，避免高墙的顶部超出画面。
        return hit.point + hit.normal * 0.12f + Vector2.up * Mathf.Max(0f, barOffset);
    }

    private void EnsureBar()
    {
        if (barRoot != null)
            return;

        GameObject root = new GameObject("ObstacleReadBar");
        root.hideFlags = HideFlags.DontSave;
        barRoot = root.transform;

        GameObject background = new GameObject("Background");
        background.hideFlags = HideFlags.DontSave;
        background.transform.SetParent(barRoot, false);
        SpriteRenderer backgroundRenderer = background.AddComponent<SpriteRenderer>();
        backgroundRenderer.sprite = barBackgroundSprite;
        backgroundRenderer.color = new Color(0f, 0f, 0f, 0.8f);
        backgroundRenderer.sortingOrder = barSortingOrder;
        background.transform.localScale = new Vector3(barWidth + 0.08f, barHeight + 0.08f, 1f);

        GameObject fill = new GameObject("Fill");
        fill.hideFlags = HideFlags.DontSave;
        fill.transform.SetParent(barRoot, false);
        barFill = fill.transform;
        barFillRenderer = fill.AddComponent<SpriteRenderer>();
        barFillRenderer.sprite = barFillSprite;
        barFillRenderer.color = barActiveColor;
        barFillRenderer.sortingOrder = barSortingOrder + 1;
        barFill.localPosition = new Vector3(-barWidth * 0.5f, 0f, 0f);
        barFill.localScale = new Vector3(0f, barHeight, 1f);
    }

    private void UpdateBarVisual()
    {
        if (barFill == null || barFillRenderer == null)
            return;

        float progress = Mathf.Clamp01(readProgress / Mathf.Max(0.05f, readDuration));
        barFill.localScale = new Vector3(barWidth * progress, barHeight, 1f);
        barFillRenderer.color = readPaused ? barPausedColor : barActiveColor;
    }

    private void UpdateBarTransform()
    {
        if (barRoot == null)
            return;

        barRoot.position = new Vector3(barPosition.x, barPosition.y, 0f);
    }

    private void CreateBarSprites()
    {
        barTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        barTexture.name = "ObstacleReadBarTexture";
        barTexture.hideFlags = HideFlags.HideAndDontSave;
        barTexture.SetPixel(0, 0, Color.white);
        barTexture.Apply();
        barTexture.wrapMode = TextureWrapMode.Clamp;
        barTexture.filterMode = FilterMode.Point;

        // 背景用中心轴，填充用左轴，缩放 x 即为进度。
        barBackgroundSprite = Sprite.Create(
            barTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        barBackgroundSprite.name = "ObstacleReadBarBackground";
        barBackgroundSprite.hideFlags = HideFlags.HideAndDontSave;

        barFillSprite = Sprite.Create(
            barTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0f, 0.5f), 1f);
        barFillSprite.name = "ObstacleReadBarFill";
        barFillSprite.hideFlags = HideFlags.HideAndDontSave;
    }

    private void OnDisable()
    {
        RestoreHighlight();
        CancelRead();
    }

    private void OnDestroy()
    {
        if (barBackgroundSprite != null)
            Destroy(barBackgroundSprite);

        if (barFillSprite != null)
            Destroy(barFillSprite);

        if (barTexture != null)
            Destroy(barTexture);
    }
}
