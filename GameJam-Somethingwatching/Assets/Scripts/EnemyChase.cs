using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChase : MonoBehaviour
{
    public Transform player;
    public Flashlight2D flashlight;
    public float moveSpeed = 3f;
    public float retreatSpeed = 4f;
    public float stopDistance = 0.6f;
    [Min(0f)] public float extraRetreatDistance = 2f;

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

    private bool flanking;
    private int lastFlankSide;
    private Vector2 flankTarget, flankOffset, flankPlayerPosition;
    private bool flankRouteReady;
    private readonly List<Vector2> chaseTrail = new List<Vector2>();
    private readonly List<Vector2> previousChaseRoute = new List<Vector2>();

    private Rigidbody2D rb;
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
    }
    void LateUpdate()
{
    if (AudioManager.Instance == null)
        return;

    if (player == null || rb == null || Time.timeScale <= 0f)
    {
        AudioManager.Instance.StopMonsterWalking();
        return;
    }

    bool isMoving = rb.linearVelocity.sqrMagnitude > 0.0001f;

    float distance = Vector2.Distance(
        rb.position, (Vector2)player.position);

    bool isRetreating = wasIlluminated || extraRetreat;

    AudioManager.Instance.SetMonsterFootsteps(
        isMoving, distance, isRetreating);
}
    void OnEnable()
    {    if (rb != null)
        rb.linearVelocity = Vector2.zero;

    if (AudioManager.Instance != null)
        AudioManager.Instance.StopMonsterWalking();
        wasIlluminated = extraRetreat = false;
        flanking = false;
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
            remainingDistance -= Vector2.Distance(rb.position, previousPosition);
        }
        previousPosition = rb.position;

        if (player == null)
        {
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
            }
            retreatThreat = (Vector2)flashlight.transform.position;
            Vector2 away = rb.position - retreatThreat;
            if (away.sqrMagnitude < 0.0001f) away = (Vector2)flashlight.transform.up;
            retreatDirection = away.normalized;
            wasIlluminated = true;
            extraRetreat = false;
            Retreat(float.PositiveInfinity);
            return;
        }

        if (wasIlluminated)
        {
            wasIlluminated = false;
            extraRetreat = true;
            remainingDistance = Mathf.Max(0f, extraRetreatDistance);
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
            nextRepath = Time.fixedTime + Mathf.Max(0.1f, repathInterval);
        }

        FollowPath(moveSpeed, float.PositiveInfinity);
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

    float RouteScore(List<Vector2> route, Vector2 start)
    {
        float score = 0f;
        Vector2 previous = start;
        foreach (Vector2 point in route)
        {
            float distance = Vector2.Distance(previous, point);
            int samples = Mathf.Max(1, Mathf.CeilToInt(distance / 0.4f));
            for (int i = 1; i <= samples; i++)
            {
                Vector2 sample = previous + (point - previous) * ((float)i / samples);
                score += distance / samples * (1f + Mathf.Max(0f, oldRoutePenalty) * PreviousRouteWeight(sample));
            }
            previous = point;
        }
        return score;
    }

    void BeginAlternateApproach()
    {
        // 找不到暗处侧路时保留这个状态，等待重算，绝不直接穿光追击。
        flanking = true;
        flankRouteReady = false;
        path.Clear();
        waypoint = 0;
        nextRepath = Time.fixedTime + Mathf.Max(0.1f, repathInterval);
        if (player == null || moveSpeed <= 0f || BodyInLight(rb.position)) return;
        Vector2 target = player.position;
        Vector2 approach = (rb.position - target).normalized;
        if (approach.sqrMagnitude < 0.0001f) approach = Vector2.up;
        Vector2 sideDirection = new Vector2(-approach.y, approach.x);
        int preferredSide = lastFlankSide == 0 ? 1 : -lastFlankSide;
        float bestScore = float.PositiveInfinity;
        List<Vector2> bestPath = null;
        int selectedSide = preferredSide;
        Vector2 selectedGoal = target;
        float tolerance = Mathf.Max(0.2f, cellSize * 0.8f);

        // 比较左右侧的候选点。堵住的一侧、墙里和地图外的点都会被排除。
        for (int sizeIndex = 0; sizeIndex < 2; sizeIndex++)
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            if (sizeIndex > 0 && bestPath != null) continue;
            int side = sideIndex == 0 ? preferredSide : -preferredSide;
            float radius = Mathf.Max(0.5f, flankDistance) * (sizeIndex == 0 ? 1f : 0.6f);
            Vector2 candidate = target + (sideDirection * side + approach * 0.25f).normalized * radius;
            if (!InMap(candidate) || BodyInLight(candidate) || Vector2.Distance(rb.position, candidate) < 0.5f) continue;
            if (Physics2D.OverlapCircle(candidate, BodyRadius, obstacleLayers) != null) continue;

            List<Vector2> candidatePath = FindRoute(rb.position, candidate, tolerance, true, false, true);
            if (candidatePath.Count == 0) continue;
            float score = RouteScore(candidatePath, rb.position);
            // 路线差不多长时，下一次优先换到另一侧。
            if (side != preferredSide) score += Mathf.Max(1f, flankDistance);
            if (sizeIndex > 0) score += 1f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestPath = candidatePath;
            selectedSide = side;
            selectedGoal = candidate;
        }
        if (bestPath == null) return; // 留在暗处等待，不强行走回光里。

        path = bestPath;
        waypoint = 0;
        flankTarget = selectedGoal;
        flankOffset = selectedGoal - target;
        flankPlayerPosition = target;
        lastFlankSide = selectedSide;
        flanking = true;
        flankRouteReady = true;
        nextRepath = Time.fixedTime + Mathf.Max(0.1f, repathInterval);
    }

    bool ContinueAlternateApproach()
    {
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
        var light = flashlight.GetComponent<UnityEngine.Rendering.Universal.Light2D>();
        if (light == null || !light.isActiveAndEnabled) return false;
        Vector2 delta = position - (Vector2)flashlight.transform.position;
        float r = BodyRadius + margin;
        float distance = delta.magnitude;
        if (distance > flashlight.range + r) return false;
        if (distance <= r) return true;
        float expansion = (float)System.Math.Asin(Mathf.Min(1f, r / distance)) * 57.29578f;
        // 绕路时保守避开整个扇形，包括墙后的扇形区域，防止擦边露出。
        return Vector2.Angle((Vector2)flashlight.transform.up, delta) <= flashlight.coneAngle * 0.5f + expansion;
    }

    bool HiddenSegment(Vector2 from, Vector2 to, float margin = 0.2f)
    {
        int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / 0.1f));
        for (int i = 0; i <= samples; i++)
            if (BodyInLight(from + (to - from) * ((float)i / samples), margin)) return false;
        return true;
    }

    void ResetPath()
    {
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
            nextRepath = Time.fixedTime + Mathf.Max(0.1f, repathInterval);
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
        while (waypoint + 1 < path.Count && SegmentClear(rb.position, path[waypoint + 1]) &&
            (!flanking || HiddenSegment(rb.position, path[waypoint + 1])))
            waypoint++;

        if (!SegmentClearWithRadius(rb.position, path[waypoint], BodyRadius) ||
            (flanking && !HiddenSegment(rb.position, path[waypoint], 0.1f)))
        {
            rb.linearVelocity = Vector2.zero;
            ResetPath();
            nextRepath = Time.fixedTime + Mathf.Max(0.1f, repathInterval);
            return;
        }
        float distance = Mathf.Min(distanceLimit, Vector2.Distance(rb.position, path[waypoint]));
        MoveTowards(path[waypoint], distance, speed);
    }

    void MoveTowards(Vector2 target, float allowedDistance, float desiredSpeed = -1f)
    {
        if (desiredSpeed < 0f) desiredSpeed = moveSpeed;
        float speed = Mathf.Min(Mathf.Max(0f, desiredSpeed), Mathf.Max(0f, allowedDistance) / Time.fixedDeltaTime);
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

    List<Vector2> FindRoute(Vector2 start, Vector2 goal, float arrivalRadius, bool avoidOldRoute, bool allowPartial, bool hiddenOnly = false)
    {
        List<Vector2> empty = new List<Vector2>();
        float step = Mathf.Max(0.1f, cellSize);
        int width = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / step));
        int height = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / step));
        // 40 x 40、格子 0.25 共 25,600 格，必须允许正常搜索。
        if ((long)width * height > 100000)
        {
            Debug.LogWarning("EnemyChase: 寻路格子超过 100000，请增大 Cell Size 或缩小 Map Size。", this);
            return empty;
        }
        Vector2 origin = mapCenter - new Vector2(width * step, height * step) * 0.5f;
        Vector2Int a = ToCell(start, origin, step);
        if (!Inside(a, width, height)) return empty;

        int count = width * height;
        float[] cost = new float[count];
        int[] parent = new int[count];
        bool[] closed = new bool[count];
        sbyte[] walkable = new sbyte[count];
        float[] wallPenalty = new float[count];
        float bodyRadius = BodyRadius;
        float preferredRadius = Mathf.Max(bodyRadius, agentRadius);
        float[] routePenalty = avoidOldRoute ? new float[count] : null;
        for (int i = 0; i < count; i++)
        {
            cost[i] = float.PositiveInfinity;
            parent[i] = -1;
        }
        int first = a.x + a.y * width;
        MinHeap open = new MinHeap();
        cost[first] = 0f;
        open.Push(first, Mathf.Max(0f, Vector2.Distance(start, goal) - arrivalRadius));
        int nearest = first, nearestVisible = -1;
        float nearestDistance = float.PositiveInfinity;
        float nearestVisibleDistance = float.PositiveInfinity;

        while (open.Count > 0)
        {
            int current = open.Pop();
            if (closed[current]) continue;
            closed[current] = true;
            Vector2 currentPos = NodePosition(current, first, start, origin, step, width);
            float distance = Vector2.Distance(currentPos, goal);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = current;
            }
            // 隔着墙靠得很近，不能算追到了玩家。
            bool visible = Physics2D.Linecast(currentPos, goal, obstacleLayers).collider == null;
            if (visible && distance < nearestVisibleDistance)
            {
                nearestVisibleDistance = distance;
                nearestVisible = current;
            }
            if (visible && distance <= arrivalRadius)
                return BuildPath(current, first, parent, start, origin, step, width);

            int x = current % width, y = current / width;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int neighbor = nx + ny * width;
                if (closed[neighbor]) continue;
                Vector2 next = NodePosition(neighbor, first, start, origin, step, width);
                if (walkable[neighbor] == 0)
                {
                    walkable[neighbor] = (sbyte)(Physics2D.OverlapCircle(next, bodyRadius, obstacleLayers) == null ? 1 : -1);
                    wallPenalty[neighbor] = Physics2D.OverlapCircle(next, preferredRadius, obstacleLayers) != null ? 4f : 0f;
                    if (avoidOldRoute) routePenalty[neighbor] = Mathf.Max(0f, oldRoutePenalty) * PreviousRouteWeight(next);
                }
                if (walkable[neighbor] < 0 || !SegmentClearWithRadius(currentPos, next, bodyRadius)) continue;
                if (hiddenOnly && !HiddenSegment(currentPos, next)) continue;
                float newCost = cost[current] + Vector2.Distance(currentPos, next) * (1f + wallPenalty[neighbor] + (avoidOldRoute ? routePenalty[neighbor] : 0f));
                if (newCost >= cost[neighbor]) continue;
                cost[neighbor] = newCost;
                parent[neighbor] = current;
                float heuristic = Mathf.Max(0f, Vector2.Distance(next, goal) - arrivalRadius);
                open.Push(neighbor, newCost + heuristic);
            }
        }
        if (!allowPartial) return empty;
        // 无法靠到指定距离时，也先走到最近的可达位置。
        int best = nearestVisible >= 0 ? nearestVisible : nearest;
        return BuildPath(best, first, parent, start, origin, step, width);
    }

    // 从当前位置搜索真实可达的逃跑位置。允许先沿墙侧移或绕过墙角。
    List<Vector2> FindRetreatPath(Vector2 start, Vector2 threat)
    {
        List<Vector2> empty = new List<Vector2>();
        float step = Mathf.Max(0.1f, cellSize);
        int width = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / step));
        int height = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / step));
        if ((long)width * height > 100000) return empty;
        Vector2 origin = mapCenter - new Vector2(width * step, height * step) * 0.5f;
        Vector2Int a = ToCell(start, origin, step);
        if (!Inside(a, width, height)) return empty;

        int count = width * height;
        float[] cost = new float[count];
        int[] parent = new int[count];
        bool[] closed = new bool[count];
        sbyte[] walkable = new sbyte[count];
        float[] wallPenalty = new float[count];
        for (int i = 0; i < count; i++)
        {
            cost[i] = float.PositiveInfinity;
            parent[i] = -1;
        }
        float bodyRadius = BodyRadius;
        float preferredRadius = PreferredRadius;
        float searchLimit = Mathf.Max(1f, retreatSearchDistance);
        float startDistance = Vector2.Distance(start, threat);
        int first = a.x + a.y * width;
        int best = first;
        float bestScore = 0.05f;
        MinHeap open = new MinHeap();
        cost[first] = 0f;
        open.Push(first, 0f);

        while (open.Count > 0)
        {
            int current = open.Pop();
            if (closed[current]) continue;
            closed[current] = true;
            Vector2 currentPos = NodePosition(current, first, start, origin, step, width);
            float gain = Vector2.Distance(currentPos, threat) - startDistance;
            bool outsideLight = wasIlluminated && flashlight != null && !flashlight.IsIlluminating(currentPos);
            // 优先远离光源；如果能离开扇形，允许少量侧移绕路。
            if (current != first && (gain > 0.1f || (outsideLight && gain >= -0.25f)))
            {
                float score = gain + (outsideLight ? 2f : 0f) - cost[current] * 0.12f - wallPenalty[current];
                if (score > bestScore)
                {
                    bestScore = score;
                    best = current;
                }
            }

            int x = current % width, y = current / width;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int neighbor = nx + ny * width;
                if (closed[neighbor]) continue;
                Vector2 next = NodePosition(neighbor, first, start, origin, step, width);
                // 先过滤搜索半径，避免查询远处的碰撞体。
                float length = Vector2.Distance(currentPos, next);
                if (cost[current] + length > searchLimit) continue;
                if (walkable[neighbor] == 0)
                {
                    walkable[neighbor] = (sbyte)(Physics2D.OverlapCircle(next, bodyRadius, obstacleLayers) == null ? 1 : -1);
                    wallPenalty[neighbor] = Physics2D.OverlapCircle(next, preferredRadius, obstacleLayers) != null ? 0.25f : 0f;
                }
                if (walkable[neighbor] < 0 || !SegmentClearWithRadius(currentPos, next, bodyRadius)) continue;
                float newCost = cost[current] + length * (1f + wallPenalty[neighbor]);
                if (newCost > searchLimit || newCost >= cost[neighbor]) continue;
                cost[neighbor] = newCost;
                parent[neighbor] = current;
                open.Push(neighbor, newCost);
            }
        }
        return BuildPath(best, first, parent, start, origin, step, width);
    }

    List<Vector2> BuildPath(int end, int first, int[] parent, Vector2 start,
        Vector2 origin, float step, int width)
    {
        List<Vector2> result = new List<Vector2>();
        for (int id = end; id != first && id >= 0; id = parent[id])
            result.Add(NodePosition(id, first, start, origin, step, width));
        result.Reverse();
        return result;
    }

    Vector2Int ToCell(Vector2 p, Vector2 origin, float step)
    {
        return new Vector2Int(Mathf.FloorToInt((p.x - origin.x) / step), Mathf.FloorToInt((p.y - origin.y) / step));
    }

    bool Inside(Vector2Int cell, int width, int height)
    {
        return cell.x >= 0 && cell.y >= 0 && cell.x < width && cell.y < height;
    }

    Vector2 NodePosition(int id, int first, Vector2 start, Vector2 origin, float step, int width)
    {
        if (id == first) return start;
        return origin + new Vector2((id % width + 0.5f) * step, (id / width + 0.5f) * step);
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
