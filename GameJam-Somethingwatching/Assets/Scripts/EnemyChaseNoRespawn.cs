using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// EnemyChase 的独立复制版（原始 EnemyChase.cs 保持不变）。
/// 被手电照到后仍会跑出相机画面，但完整离开屏幕后不再消失、也不会交给
/// MonsterRespawn 隐藏并重生；它会留在场上，从当前位置继续行动。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChaseNoRespawn1 : MonoBehaviour
{
    public Transform player;
    public Flashlight2D flashlight;
    public float moveSpeed = 3f;
    public float retreatSpeed = 4f;
    public float stopDistance = 0.6f;
    [Min(0f)] public float extraRetreatDistance = 2f;

    [Header("Escape screen / 逃离屏幕")]
    [Tooltip("被手电照到后，优先寻找一条离开玩家屏幕的路线。")]
    public bool fleeScreenWhenLit = true;
    [Tooltip("逃跑目标点超出屏幕边缘的世界距离。")]
    [Min(0f)] public float screenEscapeMargin = 3f;
    [Tooltip("用于判断屏幕范围的相机；留空自动使用 Camera.main。")]
    public Camera viewCamera;

    [Header("Pathfinding / 寻路设置")]
    public LayerMask obstacleLayers;
    public Vector2 mapCenter = Vector2.zero;
    public Vector2 mapSize = new Vector2(40f, 40f);
    [Min(0.1f)] public float cellSize = 0.5f;
    [Min(0.01f)] public float agentRadius = 0.55f;
    [Min(0.1f)] public float repathInterval = 0.4f;
    [Min(1f)] public float retreatSearchDistance = 8f; // 后退绕路时搜索多远

    [Header("Alternate approach / 后退后换路线")]
    public bool changeRouteAfterRetreat = true;
    [Min(0.5f)] public float flankDistance = 3f; // 绕向玩家侧面的距离
    [Min(0f)] public float oldRoutePenalty = 5f; // 越大越倾向避开上次路线

    [Header("Search budget / 搜索预算")]
    [Tooltip("限制每次实际搜索量，而不是整张地图的格子总数")]
    [Min(128)] public int maxSearchNodes = 12000;
    [Tooltip("每次物理更新展开的节点数。越小越平滑，但首次找到路线更慢。")]
    [Min(16)] public int searchNodesPerStep = 192;
    [Min(0.5f)] public float blockedRetreatTimeout = 2f;
    [SerializeField] private string pathStatus = "Ready";
    [SerializeField] private int lastExpandedNodes;

    private float blockedRetreatTime;
    private Flashlight2D cachedFlashlight;
    private UnityEngine.Rendering.Universal.Light2D cachedLight;
    private readonly Dictionary<long, SearchNode> searchNodes = new Dictionary<long, SearchNode>();
    private readonly List<SearchNode> activeNodes = new List<SearchNode>();
    private readonly Stack<SearchNode> nodePool = new Stack<SearchNode>();

    private IEnumerator<List<Vector2>> pendingSearch;
    private Vector2 pendingStart;
    private List<Vector2> pendingGoals;
    private float pendingArrival, pendingCellSize, pendingBodyRadius;
    private Vector2 pendingMapCenter, pendingMapSize;
    private int pendingMask;
    private bool pendingAvoid, pendingPartial, pendingHidden, pendingEscape;
    private int completedGoal;
    private Vector2 pendingLightPosition, pendingLightForward;

    private bool flanking;
    private bool fleeingScreen;
    private int lastFlankSide;
    private Vector2 flankTarget, flankPlayerPosition;
    private bool flankRouteReady;
    private readonly List<Vector2> escapeGoals = new List<Vector2>();
    private float nextScreenEscapeRetry;
    private readonly List<Vector2> chaseTrail = new List<Vector2>();
    private readonly List<Vector2> previousChaseRoute = new List<Vector2>();

    private Rigidbody2D rb;
    private SpriteRenderer spriteRenderer;
    private bool wasIlluminated, extraRetreat;
    private Vector2 retreatDirection, previousPosition, retreatThreat, plannedThreat;
    private float remainingDistance, nextRepath;
    private List<Vector2> path = new List<Vector2>();
    private int waypoint;
    private Collider2D bodyCollider;

    // 安全距离是路线偏好；身体半径才是能否通过的硬限制。
    float BodyRadius
    {
        get
        {
            if (bodyCollider is CircleCollider2D circle)
            {
                Vector3 scale = circle.transform.lossyScale;
                return Mathf.Max(0.01f, circle.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)) - 0.005f);
            }
            if (bodyCollider != null)
                return Mathf.Max(0.01f, ((Vector2)bodyCollider.bounds.extents).magnitude - 0.005f);
            return 0.49f;
        }
    }

    float PreferredRadius => Mathf.Max(BodyRadius, agentRadius);

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        bodyCollider = GetComponent<Collider2D>();
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();

        if (viewCamera == null)
            viewCamera = Camera.main;
        if (viewCamera == null)
            viewCamera = FindAnyObjectByType<Camera>();
    }

    void OnEnable()
    {
        CancelSearch();
        if (rb != null) previousPosition = rb.position;
        wasIlluminated = extraRetreat = false;
        blockedRetreatTime = 0f;
        flanking = false;
        fleeingScreen = false;
        escapeGoals.Clear();
        nextScreenEscapeRetry = 0f;
        lastFlankSide = 0;
        chaseTrail.Clear();
        previousChaseRoute.Clear();
        nextRepath = 0f;
        path.Clear();
        waypoint = 0;
    }

    void FixedUpdate()
    {
        if (extraRetreat)
        {
            // 绕墙时会转弯，额外后退按实际走过的路程计算。
            // 被墙挡住没有移动，就不会消耗额外后退距离。
            float moved = Vector2.Distance(rb.position, previousPosition);
            remainingDistance -= moved;
            blockedRetreatTime = moved > 0.001f || pendingSearch != null ? 0f : blockedRetreatTime + Time.fixedDeltaTime;
            if (blockedRetreatTime >= Mathf.Max(0.5f, blockedRetreatTimeout))
            {
                remainingDistance = 0f;
                pathStatus = "Extra retreat blocked; trying hidden approach";
            }
        }
        previousPosition = rb.position;

        if (player == null)
        {
            CancelSearch();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        bool illuminated = flashlight != null && flashlight.IsIlluminating(rb.position);
        if (illuminated)
        {
            if (!wasIlluminated)
            {
                // 同一次逃跑途中再次被照到，不覆盖原来的追击路线记忆。
                if (!extraRetreat) RememberChaseRoute();
                flanking = false;
                ResetPath();

                if (fleeScreenWhenLit)
                {
                    fleeingScreen = true;
                    escapeGoals.Clear();
                    nextScreenEscapeRetry = 0f;
                }
            }
            retreatThreat = (Vector2)flashlight.transform.position;
            Vector2 away = rb.position - retreatThreat;
            if (away.sqrMagnitude < 0.0001f) away = (Vector2)flashlight.transform.up;
            retreatDirection = away.normalized;
            wasIlluminated = true;
            extraRetreat = false;
            blockedRetreatTime = 0f;

            if (fleeingScreen)
            {
                FleeScreen();
                return;
            }

            Retreat(float.PositiveInfinity);
            return;
        }

        if (fleeingScreen)
        {
            // 即使手电已移开，也要先完成“跑出屏幕”，然后恢复普通行动。
            FleeScreen();
            return;
        }

        if (wasIlluminated)
        {
            wasIlluminated = false;
            extraRetreat = true;
            remainingDistance = Mathf.Max(0f, extraRetreatDistance);
            blockedRetreatTime = 0f;
            // 保留正在绕墙的路线，离开光照时不会突然掉头。
        }
        if (extraRetreat && remainingDistance > 0.01f)
        {
            Retreat(remainingDistance);
            return;
        }
        if (extraRetreat)
        {
            extraRetreat = false;
            ResetPath();
            if (changeRouteAfterRetreat) BeginAlternateApproach();
            chaseTrail.Clear();
            if (flanking)
            {
                // 本次更新已经启动搜索，下一次物理更新再继续，避免重复消耗预算。
                rb.linearVelocity = Vector2.zero;
                return;
            }
        }

        if (flanking && ContinueAlternateApproach()) return;

        RecordChasePosition();
        Vector2 target = (Vector2)player.position;
        float distance = Vector2.Distance(rb.position, target);
        if (distance <= Mathf.Max(0f, stopDistance) &&
            Physics2D.Linecast(rb.position, target, obstacleLayers).collider == null)
        {
            flanking = false;
            ResetPath();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // 没有墙挡路时直接追赶，避免不必要的格子移动。
        if (SegmentClear(rb.position, target))
        {
            CancelSearch();
            // 清掉旧绕墙路线，下一次记忆应记录当前实际的直线追击。
            path.Clear();
            waypoint = 0;
            MoveTowards(target, distance - Mathf.Max(0f, stopDistance));
            nextRepath = 0f;
            return;
        }

        if (Time.fixedTime >= nextRepath)
        {
            path = FindPath(rb.position, target);
            waypoint = 0;
            nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
        }

        FollowPath(moveSpeed, float.PositiveInfinity);
    }

    // 被光照到后：优先跑出相机画面；离开屏幕后不隐藏、不重生，而是留在场上继续行动。
    void FleeScreen()
    {
        if (!IsOnScreen())
        {
            fleeingScreen = false;
            ResetPath();
            escapeGoals.Clear();
            nextScreenEscapeRetry = 0f;
            // 关键改动：不再调用 MonsterRespawn.Despawn()。
            // 怪物已经完整离开屏幕，保持存活，从当前位置恢复追击；
            // 手电仍照着它时，下一帧会继续按普通后退逻辑远离光源。
            return;
        }

        if (path.Count == 0 || waypoint >= path.Count)
        {
            if (Time.fixedTime < nextScreenEscapeRetry)
            {
                Retreat(float.PositiveInfinity);
                return;
            }

            if (escapeGoals.Count == 0)
            {
                BuildEscapeGoals(escapeGoals);
                if (escapeGoals.Count == 0)
                {
                    // 没有可用的屏幕外目标（例如相机为空），退回普通后退。
                    Retreat(float.PositiveInfinity);
                    return;
                }
            }

            List<Vector2> result = SearchSparse(
                rb.position, escapeGoals, 0.5f, false, true, false, false, out _);

            if (result.Count > 0)
            {
                path = result;
                waypoint = 0;
                nextScreenEscapeRetry = 0f;
                escapeGoals.Clear();
            }
            else if (pendingSearch != null)
            {
                // 分帧搜索进行中，先停下让起点保持稳定。
                rb.linearVelocity = Vector2.zero;
                return;
            }
            else
            {
                // 本次没有搜索到路径，短暂退回普通后退，稍后再试。
                escapeGoals.Clear();
                nextScreenEscapeRetry = Time.fixedTime + 0.5f;
                Retreat(float.PositiveInfinity);
                return;
            }
        }

        FollowPath(retreatSpeed, float.PositiveInfinity);
    }

    // 生成屏幕外目标点：优先取当前方向最近的四条屏幕边，地图边缘外再随机兜底。
    void BuildEscapeGoals(List<Vector2> goals)
    {
        goals.Clear();
        if (viewCamera == null)
            return;

        Vector2 camera = viewCamera.transform.position;
        float halfHeight = viewCamera.orthographicSize;
        float halfWidth = halfHeight * viewCamera.aspect;
        float margin = Mathf.Max(0.5f, screenEscapeMargin);
        Vector2 position = rb.position;

        Vector2 mapMin = mapCenter - mapSize * 0.5f;
        Vector2 mapMax = mapCenter + mapSize * 0.5f;
        float edge = BodyRadius + 0.05f;

        float signX = position.x >= camera.x ? 1f : -1f;
        float signY = position.y >= camera.y ? 1f : -1f;
        float clampedX = Mathf.Clamp(position.x, mapMin.x + edge, mapMax.x - edge);
        float clampedY = Mathf.Clamp(position.y, mapMin.y + edge, mapMax.y - edge);

        AddEscapeGoal(goals, new Vector2(camera.x + signX * (halfWidth + margin), clampedY));
        AddEscapeGoal(goals, new Vector2(camera.x - signX * (halfWidth + margin), clampedY));
        AddEscapeGoal(goals, new Vector2(clampedX, camera.y + signY * (halfHeight + margin)));
        AddEscapeGoal(goals, new Vector2(clampedX, camera.y - signY * (halfHeight + margin)));

        // 地图边缘导致四边目标都不可用时，随机找一个地图内、屏幕外的点。
        if (goals.Count == 0)
        {
            for (int i = 0; i < 64; i++)
            {
                Vector2 candidate = new Vector2(
                    Random.Range(mapMin.x + edge, mapMax.x - edge),
                    Random.Range(mapMin.y + edge, mapMax.y - edge));
                if (!IsOffScreen(candidate))
                    continue;
                if (Physics2D.OverlapCircle(candidate, BodyRadius, obstacleLayers) != null)
                    continue;

                goals.Add(candidate);
                break;
            }
        }
    }

    void AddEscapeGoal(List<Vector2> goals, Vector2 goal)
    {
        if (!InMap(goal))
            return;
        if (!IsOffScreen(goal))
            return;
        if (Physics2D.OverlapCircle(goal, BodyRadius, obstacleLayers) != null)
            return;

        for (int i = 0; i < goals.Count; i++)
            if (Vector2.Distance(goals[i], goal) < 0.5f)
                return;

        goals.Add(goal);
    }

    bool IsOnScreen()
    {
        if (viewCamera == null)
            return true;

        // 用 Sprite 的实际包围盒判断，确保整个身体都离开屏幕后才算离屏。
        if (spriteRenderer != null)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(viewCamera);
            if (GeometryUtility.TestPlanesAABB(planes, spriteRenderer.bounds))
                return true;
        }

        return !IsOffScreen(rb.position);
    }

    bool IsOffScreen(Vector2 point)
    {
        if (viewCamera == null)
            return false;

        Vector3 viewport = viewCamera.WorldToViewportPoint(point);
        return viewport.z <= 0f ||
            viewport.x < 0f || viewport.x > 1f ||
            viewport.y < 0f || viewport.y > 1f;
    }

    /// <summary>
    /// 可达性验证：从 start 出发，是否存在一条完整可达玩家的寻路路径。
    /// 使用与 AI 相同的网格、身体半径和障碍层；预算耗尽视为不可达。
    /// </summary>
    public bool CanPathToPlayerFrom(Vector2 start, int nodeBudget = 12000)
    {
        if (player == null)
            return false;

        float step = Mathf.Max(0.1f, cellSize);
        int width = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / step));
        int height = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / step));
        if ((long)width * height > 200000)
            return false;

        Vector2 origin = mapCenter - mapSize * 0.5f;
        Vector2 goal = player.position;
        float arrivalRadius = Mathf.Max(0.15f, stopDistance) + step * 0.71f;
        float body = BodyRadius;

        if (Physics2D.OverlapCircle(start, body, obstacleLayers) != null)
            return false;

        int startX = Mathf.FloorToInt((start.x - origin.x) / step);
        int startY = Mathf.FloorToInt((start.y - origin.y) / step);
        if (startX < 0 || startY < 0 || startX >= width || startY >= height)
            return false;

        int count = width * height;
        float[] cost = new float[count];
        bool[] closed = new bool[count];
        sbyte[] walkable = new sbyte[count];
        for (int i = 0; i < count; i++)
            cost[i] = float.PositiveInfinity;

        int first = startX + startY * width;
        cost[first] = 0f;
        MinHeap open = new MinHeap();
        open.Push(first, Mathf.Max(0f, Vector2.Distance(start, goal) - arrivalRadius));
        int expanded = 0;
        int budget = Mathf.Max(128, nodeBudget);

        while (open.Count > 0 && expanded < budget)
        {
            int current = open.Pop();
            if (closed[current])
                continue;
            closed[current] = true;
            expanded++;

            int cx = current % width;
            int cy = current / width;
            Vector2 currentPos = (cx == startX && cy == startY)
                ? start
                : origin + new Vector2((cx + 0.5f) * step, (cy + 0.5f) * step);

            if (Vector2.Distance(currentPos, goal) <= arrivalRadius &&
                Physics2D.Linecast(currentPos, goal, obstacleLayers).collider == null)
                return true;

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    continue;

                int next = nx + ny * width;
                if (closed[next])
                    continue;

                Vector2 nextPos = origin + new Vector2((nx + 0.5f) * step, (ny + 0.5f) * step);
                if (walkable[next] == 0)
                    walkable[next] = (sbyte)(Physics2D.OverlapCircle(nextPos, body, obstacleLayers) == null ? 1 : -1);
                if (walkable[next] < 0)
                    continue;
                if (!SegmentClearWithRadius(currentPos, nextPos, body))
                    continue;

                float newCost = cost[current] + Vector2.Distance(currentPos, nextPos);
                if (newCost >= cost[next])
                    continue;

                cost[next] = newCost;
                float heuristic = Mathf.Max(0f, Vector2.Distance(nextPos, goal) - arrivalRadius);
                open.Push(next, newCost + heuristic);
            }
        }

        return false;
    }

    void RecordChasePosition()
    {
        if (chaseTrail.Count == 0 || Vector2.Distance(chaseTrail[chaseTrail.Count - 1], rb.position) >= 0.4f)
        {
            chaseTrail.Add(rb.position);
            if (chaseTrail.Count > 32) chaseTrail.RemoveAt(0);
        }
    }

    void RememberChaseRoute()
    {
        previousChaseRoute.Clear();
        previousChaseRoute.AddRange(chaseTrail);
        previousChaseRoute.Add(rb.position);
        for (int i = waypoint; i < path.Count; i++)
            previousChaseRoute.Add(path[i]);
        if (player != null) previousChaseRoute.Add((Vector2)player.position);
    }

    // 路线记忆只是增加代价，不把旧路变成墙。只有一条路时仍然能通行。
    float PreviousRouteWeight(Vector2 point)
    {
        float width = Mathf.Max(1f, PreferredRadius * 1.5f);
        float nearest = float.PositiveInfinity;
        for (int i = 1; i < previousChaseRoute.Count; i++)
        {
            Vector2 a = previousChaseRoute[i - 1];
            Vector2 delta = previousChaseRoute[i] - a;
            float t = delta.sqrMagnitude > 0.000001f ? Vector2.Dot(point - a, delta) / delta.sqrMagnitude : 0f;
            t = Mathf.Max(0f, Mathf.Min(1f, t));
            nearest = Mathf.Min(nearest, Vector2.Distance(point, a + delta * t));
        }
        return Mathf.Max(0f, 1f - nearest / width);
    }

    void BeginAlternateApproach()
    {
        // 找不到暗处侧路时保留这个状态，等待重算，绝不直接穿光追击。
        flanking = true;
        flankRouteReady = false;
        path.Clear();
        waypoint = 0;
        nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
        if (player == null || moveSpeed <= 0f) return;
        if (BodyInLight(rb.position, 0.25f))
        {
            pathStatus = "Leaving flashlight edge before flanking";
            return;
        }
        Vector2 target = player.position;
        Vector2 approach = (rb.position - target).normalized;
        if (approach.sqrMagnitude < 0.0001f) approach = Vector2.up;
        Vector2 sideDirection = new Vector2(-approach.y, approach.x);
        int preferredSide = lastFlankSide == 0 ? 1 : -lastFlankSide;
        float tolerance = Mathf.Max(0.2f, cellSize * 0.8f);
        List<Vector2> goals = new List<Vector2>();
        List<int> sides = new List<int>();
        // 多个暗处候选点共用一次搜索，避免反复扫描同一区域。
        float[] angles = { 75f, 105f, 135f };
        float[] radii = { 1f, 1.6f, 0.6f };
        foreach (float radiusScale in radii)
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        foreach (float degrees in angles)
        {
            int side = sideIndex == 0 ? preferredSide : -preferredSide;
            float radians = degrees * Mathf.Deg2Rad;
            Vector2 direction = approach * Mathf.Cos(radians) + sideDirection * (side * Mathf.Sin(radians));
            Vector2 candidate = target + direction * Mathf.Max(0.5f, flankDistance) * radiusScale;
            if (!InMap(candidate) || BodyInLight(candidate, 0.2f)) continue;
            if (Vector2.Distance(rb.position, candidate) < tolerance * 2f) continue;
            if (Physics2D.OverlapCircle(candidate, BodyRadius, obstacleLayers) != null) continue;
            goals.Add(candidate);
            sides.Add(side);
        }
        if (goals.Count == 0)
        {
            pathStatus = "Waiting in darkness: no safe flank destination";
            return;
        }
        List<Vector2> bestPath = SearchSparse(rb.position, goals, tolerance, true, false, true, false, out int selected);
        nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
        if (bestPath.Count == 0 || selected < 0)
        {
            if (pendingSearch == null)
                pathStatus = "Waiting in darkness: no complete hidden route (or budget reached)";
            return;
        }
        path = bestPath;
        waypoint = 0;
        flankTarget = bestPath[bestPath.Count - 1];
        flankPlayerPosition = target;
        lastFlankSide = sides[selected];
        flanking = true;
        flankRouteReady = true;
        nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
    }

    bool ContinueAlternateApproach()
    {
        if (BodyInLight(rb.position, flankRouteReady ? 0.1f : 0.25f))
        {
            // 中心已离开光束，但身体擦边时先继续退。
            flanking = false;
            extraRetreat = true;
            remainingDistance = Mathf.Max(0.5f, extraRetreatDistance);
            blockedRetreatTime = 0f;
            retreatThreat = flashlight.transform.position;
            ResetPath();
            Retreat(remainingDistance);
            return true;
        }
        float tolerance = Mathf.Max(0.2f, cellSize * 0.8f);
        bool playerMoved = Vector2.Distance((Vector2)player.position, flankPlayerPosition) > 0.75f;
        if (flankRouteReady && !playerMoved &&
            !BodyInLight(rb.position) && Vector2.Distance(rb.position, flankTarget) <= tolerance)
        {
            // 只有真正到达暗处的新攻击方位后，才恢复追击；没有超时穿光捷径。
            flanking = false;
            ResetPath();
            return false;
        }
        bool routeInvalid = !flankRouteReady || waypoint >= path.Count ||
            BodyInLight(flankTarget) ||
            !HiddenSegment(rb.position, path[waypoint], 0.1f);
        if (Time.fixedTime >= nextRepath && (playerMoved || routeInvalid))
            BeginAlternateApproach();
        if (!flankRouteReady)
        {
            rb.linearVelocity = Vector2.zero;
            return true;
        }
        FollowPath(moveSpeed, float.PositiveInfinity);
        return true;
    }

    // 中心和身体边缘都检查，避免中心在暗处但半个身体露在光里。
    bool BodyInLight(Vector2 position, float margin = 0.1f)
    {
        if (flashlight == null || !flashlight.isActiveAndEnabled) return false;
        if (cachedFlashlight != flashlight || cachedLight == null)
        {
            cachedFlashlight = flashlight;
            cachedLight = flashlight.GetComponent<UnityEngine.Rendering.Universal.Light2D>();
        }
        if (cachedLight == null || !cachedLight.isActiveAndEnabled || cachedLight.intensity <= 0f) return false;
        Vector2 delta = position - (Vector2)flashlight.transform.position;
        float r = BodyRadius + margin;
        float distance = delta.magnitude;
        if (distance > flashlight.range + r) return false;
        if (distance > r)
        {
            float expansion = (float)System.Math.Asin(Mathf.Min(1f, r / distance)) * 57.29578f;
            if (Vector2.Angle((Vector2)flashlight.transform.up, delta) > flashlight.coneAngle * 0.5f + expansion)
                return false;
        }
        if (flashlight.IsIlluminating(position)) return true;
        // 墙后的阴影可用于绕行；检测边缘，降低半个身体露光的情况。
        for (int i = 0; i < 16; i++)
        {
            float angle = i * Mathf.PI / 8f;
            Vector2 edge = position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
            if (flashlight.IsIlluminating(edge)) return true;
        }
        return false;
    }

    bool HiddenSegment(Vector2 from, Vector2 to, float margin = 0.2f)
    {
        int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / 0.1f));
        for (int i = 0; i <= samples; i++)
            if (BodyInLight(from + (to - from) * ((float)i / samples), margin)) return false;
        return true;
    }

    void CancelSearch()
    {
        if (pendingSearch != null) pendingSearch.Dispose();
        pendingSearch = null;
    }

    void ResetPath()
    {
        CancelSearch();
        path.Clear();
        waypoint = 0;
        nextRepath = 0f;
    }

    void Retreat(float distanceLimit)
    {
        Vector2 away = rb.position - retreatThreat;
        away = away.sqrMagnitude > 0.0001f ? away.normalized : retreatDirection;
        if (away.sqrMagnitude < 0.0001f) away = Vector2.up;

        // 有路就沿原来的路线绕，避免每帧左右换方向。
        // 背后开阔时直接退，提前探测墙壁而非撞上后仍然顶墙。
        float probeDistance = Mathf.Max(1.25f, PreferredRadius * 2f);
        Vector2 probeTarget = rb.position + away * probeDistance;
        if (waypoint >= path.Count && InMap(probeTarget) && SegmentClear(rb.position, probeTarget))
        {
            MoveTowards(probeTarget, Mathf.Min(probeDistance, distanceLimit), retreatSpeed);
            return;
        }

        bool threatMoved = Vector2.Distance(retreatThreat, plannedThreat) > 1f;
        if (Time.fixedTime >= nextRepath && (waypoint >= path.Count || threatMoved))
        {
            path = FindRetreatPath(rb.position, retreatThreat);
            waypoint = 0;
            plannedThreat = retreatThreat;
            nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
        }
        FollowPath(retreatSpeed, distanceLimit);
    }

    bool InMap(Vector2 point)
    {
        Vector2 offset = point - mapCenter;
        return Mathf.Abs(offset.x) < mapSize.x * 0.5f && Mathf.Abs(offset.y) < mapSize.y * 0.5f;
    }

    void FollowPath(float speed, float distanceLimit)
    {
        while (waypoint < path.Count && Vector2.Distance(rb.position, path[waypoint]) < 0.025f)
            waypoint++;

        if (waypoint >= path.Count)
        {
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // 跳过可直接抵达的拐点，路线更自然。
        int shortcutChecks = 0;
        while (waypoint + 1 < path.Count && shortcutChecks++ < 12 && SegmentClear(rb.position, path[waypoint + 1]) &&
            (!flanking || HiddenSegment(rb.position, path[waypoint + 1])))
            waypoint++;

        if (!SegmentClearWithRadius(rb.position, path[waypoint], BodyRadius) ||
            (flanking && !HiddenSegment(rb.position, path[waypoint], 0.1f)))
        {
            rb.linearVelocity = Vector2.zero;
            ResetPath();
            nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
            return;
        }
        float distance = Mathf.Min(distanceLimit, Vector2.Distance(rb.position, path[waypoint]));
        MoveTowards(path[waypoint], distance, speed);
    }

    void MoveTowards(Vector2 target, float allowedDistance, float desiredSpeed = -1f)
    {
        if (desiredSpeed < 0f) desiredSpeed = moveSpeed;
        float speed = Mathf.Min(Mathf.Max(0f, desiredSpeed), Mathf.Max(0f, allowedDistance) / Time.fixedDeltaTime);
        rb.WakeUp();
        rb.linearVelocity = (target - rb.position).normalized * speed;
    }

    bool SegmentClear(Vector2 from, Vector2 to)
    {
        return SegmentClearWithRadius(from, to, PreferredRadius);
    }

    bool SegmentClearWithRadius(Vector2 from, Vector2 to, float radius)
    {
        if (Physics2D.OverlapCircle(from, radius, obstacleLayers) != null) return false;
        Vector2 delta = to - from;
        if (delta.sqrMagnitude < 0.000001f) return true;
        return Physics2D.CircleCast(from, radius, delta.normalized, delta.magnitude, obstacleLayers).collider == null;
    }

    List<Vector2> FindPath(Vector2 start, Vector2 goal)
    {
        float arrivalRadius = Mathf.Max(0f, stopDistance) + Mathf.Max(0.1f, cellSize) * 0.71f;
        return FindRoute(start, goal, arrivalRadius, false, true);
    }

    List<Vector2> FindRoute(Vector2 start, Vector2 goal, float arrivalRadius,
        bool avoidOldRoute, bool allowPartial, bool hiddenOnly = false)
    {
        return SearchSparse(start, new List<Vector2> { goal }, arrivalRadius,
            avoidOldRoute, allowPartial, hiddenOnly, false, out _);
    }

    List<Vector2> FindRetreatPath(Vector2 start, Vector2 threat)
    {
        return SearchSparse(start, new List<Vector2> { threat }, 0f,
            false, true, false, true, out _);
    }

    // 稀疏节点：地图大小只定义边界，不为未搜索的格子分配数组。
    private sealed class SearchNode
    {
        public int x, y, index;
        public Vector2 position;
        public float cost, wallPenalty, oldPenalty;
        public bool closed;
        public sbyte walkable;
        public SearchNode parent;
    }

    long CellKey(int x, int y)
    {
        return ((long)x << 32) | (uint)y;
    }

    SearchNode GetNode(int x, int y, Vector2 origin, float step)
    {
        long key = CellKey(x, y);
        if (searchNodes.TryGetValue(key, out SearchNode node)) return node;
        node = nodePool.Count > 0 ? nodePool.Pop() : new SearchNode();
        node.x = x; node.y = y; node.index = activeNodes.Count;
        node.position = origin + new Vector2((x + 0.5f) * step, (y + 0.5f) * step);
        node.cost = float.PositiveInfinity;
        node.wallPenalty = node.oldPenalty = 0f;
        node.walkable = 0; node.closed = false; node.parent = null;
        searchNodes.Add(key, node);
        activeNodes.Add(node);
        return node;
    }

    void ClearSearch()
    {
        foreach (SearchNode node in activeNodes)
        {
            node.parent = null;
            nodePool.Push(node);
        }
        activeNodes.Clear();
        searchNodes.Clear();
        lastExpandedNodes = 0;
    }

    float GoalDistance(Vector2 position, List<Vector2> goals, out int selected)
    {
        float best = float.PositiveInfinity;
        selected = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            float distance = Vector2.Distance(position, goals[i]);
            if (distance < best) { best = distance; selected = i; }
        }
        return best;
    }

    List<Vector2> TracePath(SearchNode end)
    {
        List<Vector2> result = new List<Vector2>();
        for (SearchNode node = end; node != null && node.parent != null; node = node.parent)
            result.Add(node.position);
        result.Reverse();
        return result;
    }

    List<Vector2> SearchSparse(Vector2 start, List<Vector2> goals, float arrivalRadius,
        bool avoidOldRoute, bool allowPartial, bool hiddenOnly, bool escape, out int selectedGoal)
    {
        bool same = pendingSearch != null &&
            Vector2.Distance(start, pendingStart) < 0.05f &&
            goals.Count == pendingGoals.Count &&
            arrivalRadius == pendingArrival && avoidOldRoute == pendingAvoid &&
            allowPartial == pendingPartial && hiddenOnly == pendingHidden &&
            escape == pendingEscape && cellSize == pendingCellSize &&
            Vector2.Distance(mapCenter, pendingMapCenter) < 0.001f &&
            Vector2.Distance(mapSize, pendingMapSize) < 0.001f &&
            obstacleLayers.value == pendingMask && Mathf.Abs(BodyRadius - pendingBodyRadius) < 0.001f;
        if (same)
        {
            // 普通追击先完成这轮快照搜索，避免玩家持续跑动导致每帧重启。
            // 逃跑/暗处绕行对威胁方位更敏感，需要在明显变化时重新计划。
            float goalTolerance = escape ? 1f : (hiddenOnly ? 0.75f : float.PositiveInfinity);
            for (int i = 0; i < goals.Count; i++)
                if (Vector2.Distance(goals[i], pendingGoals[i]) > goalTolerance) { same = false; break; }
            if (hiddenOnly && flashlight != null &&
                (Vector2.Distance((Vector2)flashlight.transform.position, pendingLightPosition) > 0.3f ||
                 Vector2.Angle((Vector2)flashlight.transform.up, pendingLightForward) > 8f))
                same = false;
        }
        if (!same)
        {
            CancelSearch();
            pendingStart = start;
            pendingGoals = new List<Vector2>(goals);
            pendingArrival = arrivalRadius;
            pendingAvoid = avoidOldRoute; pendingPartial = allowPartial;
            pendingHidden = hiddenOnly; pendingEscape = escape;
            pendingCellSize = cellSize;
            pendingMapCenter = mapCenter; pendingMapSize = mapSize;
            pendingMask = obstacleLayers.value; pendingBodyRadius = BodyRadius;
            if (flashlight != null)
            {
                pendingLightPosition = flashlight.transform.position;
                pendingLightForward = flashlight.transform.up;
            }
            completedGoal = -1;
            pendingSearch = SearchSparseSteps(start, pendingGoals, arrivalRadius,
                avoidOldRoute, allowPartial, hiddenOnly, escape);
        }
        selectedGoal = -1;
        if (!pendingSearch.MoveNext())
        {
            CancelSearch();
            return new List<Vector2>();
        }
        List<Vector2> result = pendingSearch.Current;
        if (result == null)
        {
            pathStatus = "Searching over multiple physics updates";
            return new List<Vector2>();
        }
        selectedGoal = completedGoal;
        CancelSearch();
        return result;
    }

    IEnumerator<List<Vector2>> SearchSparseSteps(Vector2 start, List<Vector2> goals, float arrivalRadius,
        bool avoidOldRoute, bool allowPartial, bool hiddenOnly, bool escape)
    {
        ClearSearch();
        completedGoal = -1;
        List<Vector2> empty = new List<Vector2>();
        if (!InMap(start))
        {
            pathStatus = "Outside Map Center/Map Size bounds";
            yield return empty; yield break;
        }
        float step = Mathf.Max(0.1f, cellSize);
        Vector2 origin = mapCenter - mapSize * 0.5f;
        int firstX = Mathf.FloorToInt((start.x - origin.x) / step);
        int firstY = Mathf.FloorToInt((start.y - origin.y) / step);
        SearchNode first = GetNode(firstX, firstY, origin, step);
        first.position = start;
        first.cost = 0f;
        float body = BodyRadius;
        float preferred = PreferredRadius;
        if (Physics2D.OverlapCircle(start, body, obstacleLayers) != null)
        {
            pathStatus = "Body overlaps Obstacle: check floor/layer/collider size";
            yield return empty; yield break;
        }
        if (hiddenOnly && BodyInLight(start)) { yield return empty; yield break; }
        MinHeap open = new MinHeap();
        float firstDistance = GoalDistance(start, goals, out _);
        open.Push(first.index, escape ? 0f : Mathf.Max(0f, firstDistance - arrivalRadius));
        SearchNode nearest = first, nearestVisible = null, safest = first;
        float nearestDistance = firstDistance;
        float visibleDistance = float.PositiveInfinity, bestEscapeScore = 0.05f;
        float searchLimit = Mathf.Max(1f, retreatSearchDistance);
        int budget = Mathf.Max(128, maxSearchNodes);

        int thisStep = 0;
        while (open.Count > 0 && lastExpandedNodes < budget)
        {
            if (thisStep >= Mathf.Max(16, searchNodesPerStep))
            {
                thisStep = 0;
                yield return null;
            }
            SearchNode current = activeNodes[open.Pop()];
            if (current.closed) continue;
            current.closed = true;
            lastExpandedNodes++;
            thisStep++;
            float distance = GoalDistance(current.position, goals, out int goalIndex);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = current;
            }
            if (escape)
            {
                float gain = distance - firstDistance;
                bool outsideLight = wasIlluminated && flashlight != null && !BodyInLight(current.position);
                if (current != first && (gain > 0.1f || (outsideLight && gain >= -0.25f)))
                {
                    float score = gain + (outsideLight ? 2f : 0f) - current.cost * 0.12f - current.wallPenalty;
                    if (score > bestEscapeScore) { bestEscapeScore = score; safest = current; }
                }
            }
            else
            {
                bool needsVisibility = distance <= arrivalRadius || distance < visibleDistance;
                bool visible = needsVisibility && Physics2D.Linecast(current.position, goals[goalIndex], obstacleLayers).collider == null;
                if (visible && distance < visibleDistance)
                { visibleDistance = distance; nearestVisible = current; }
                if (visible && distance <= arrivalRadius)
                {
                    completedGoal = goalIndex;
                    pathStatus = "Complete route";
                    yield return TracePath(current); yield break;
                }
            }
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = current.x + dx, ny = current.y + dy;
                Vector2 position = origin + new Vector2((nx + 0.5f) * step, (ny + 0.5f) * step);
                if (!InMap(position)) continue;
                float length = Vector2.Distance(current.position, position);
                if (escape && current.cost + length > searchLimit) continue;
                SearchNode next = GetNode(nx, ny, origin, step);
                if (next.closed) continue;
                length = Vector2.Distance(current.position, next.position);
                if (next.walkable == 0)
                {
                    next.walkable = (sbyte)(Physics2D.OverlapCircle(next.position, body, obstacleLayers) == null ? 1 : -1);
                    next.wallPenalty = Physics2D.OverlapCircle(next.position, preferred, obstacleLayers) != null ? (escape ? 0.25f : 4f) : 0f;
                    if (avoidOldRoute) next.oldPenalty = Mathf.Max(0f, oldRoutePenalty) * PreviousRouteWeight(next.position);
                    if (hiddenOnly && BodyInLight(next.position, 0.2f)) next.walkable = -1;
                }
                if (next.walkable < 0) continue;
                float cost = current.cost + length * (1f + next.wallPenalty + next.oldPenalty);
                if ((escape && cost > searchLimit) || cost >= next.cost) continue;
                if (!SegmentClearWithRadius(current.position, next.position, body)) continue;
                if (hiddenOnly && !HiddenSegment(current.position, next.position)) continue;
                next.cost = cost;
                next.parent = current;
                float heuristic = escape ? 0f : Mathf.Max(0f, GoalDistance(next.position, goals, out _) - arrivalRadius);
                open.Push(next.index, cost + heuristic);
            }
        }
        if (escape)
        {
            pathStatus = safest == first ? "No reachable retreat position" : "Retreat route";
            yield return TracePath(safest); yield break;
        }
        if (!allowPartial)
        {
            pathStatus = open.Count > 0 ? "Hidden search budget reached; waiting safely" : "No hidden route";
            yield return empty; yield break;
        }
        SearchNode best = nearestVisible != null && visibleDistance <= nearestDistance + step * 2f ? nearestVisible : nearest;
        pathStatus = open.Count > 0 ? "Partial route: search budget reached" : "Closest reachable route";
        yield return TracePath(best);
    }

    // 最小堆避免每次从所有待搜索格子中逐一找最小值。
    private class MinHeap
    {
        private readonly List<KeyValuePair<int, float>> items = new List<KeyValuePair<int, float>>();
        public int Count => items.Count;

        public void Push(int id, float priority)
        {
            items.Add(new KeyValuePair<int, float>(id, priority));
            int index = items.Count - 1;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (items[parent].Value <= items[index].Value) break;
                Swap(parent, index);
                index = parent;
            }
        }

        public int Pop()
        {
            int id = items[0].Key;
            int last = items.Count - 1;
            items[0] = items[last];
            items.RemoveAt(last);
            int index = 0;
            while (index * 2 + 1 < items.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < items.Count && items[child + 1].Value < items[child].Value) child++;
                if (items[index].Value <= items[child].Value) break;
                Swap(index, child);
                index = child;
            }
            return id;
        }

        private void Swap(int a, int b)
        {
            KeyValuePair<int, float> value = items[a];
            items[a] = items[b];
            items[b] = value;
        }
    }

    void OnDisable()
    {
        CancelSearch();
        if (rb != null) rb.linearVelocity = Vector2.zero;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(mapCenter, new Vector3(mapSize.x, mapSize.y, 0f));
        Gizmos.color = Color.green;
        if (path == null) return;
        for (int i = waypoint; i + 1 < path.Count; i++)
            Gizmos.DrawLine(path[i], path[i + 1]);
    }
}
