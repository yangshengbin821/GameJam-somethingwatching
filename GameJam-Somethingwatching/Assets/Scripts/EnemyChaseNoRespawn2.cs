using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// EnemyChase 的独立复制版（原始 EnemyChase.cs 保持不变）。
/// 行为：
/// - 屏幕外向玩家靠近时保持最高速；从屏幕边缘进入画面后，速度从 moveSpeed
///   开始按 acceleration 提升，最高 maxSpeed；
/// - 被手电照到时不逃跑，而是原地停住并把速度归零；手电移开后从 0 重新加速。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChaseNoRespawn : MonoBehaviour
{
    [Header("References / 引用")]
    public Transform player;
    public Flashlight2D flashlight;
    [Tooltip("用于判断屏幕范围的相机；留空自动使用 Camera.main。")]
    public Camera viewCamera;

    [Header("Chase speed / 追击速度")]
    [Tooltip("正常进入屏幕时的初始速度（被光停住后会从 0 重新加速）。")]
    [Min(0f)] public float moveSpeed = 3f;
    [Tooltip("每秒增加的移动速度。")]
    [Min(0f)] public float acceleration = 0.25f;
    [Tooltip("移动速度上限。")]
    [Min(0f)] public float maxSpeed = 5.5f;
    [Tooltip("距离玩家多近时停下。")]
    [Min(0f)] public float stopDistance = 0.6f;

    [Header("Pathfinding / 寻路设置")]
    public LayerMask obstacleLayers;
    public Vector2 mapCenter = Vector2.zero;
    public Vector2 mapSize = new Vector2(40f, 40f);
    [Min(0.1f)] public float cellSize = 0.5f;
    [Min(0.01f)] public float agentRadius = 0.55f;
    [Min(0.1f)] public float repathInterval = 0.4f;

    [Header("Search budget / 搜索预算")]
    [Tooltip("限制每次实际搜索量，而不是整张地图的格子总数")]
    [Min(128)] public int maxSearchNodes = 12000;
    [Tooltip("每次物理更新展开的节点数。越小越平滑，但首次找到路线更慢。")]
    [Min(16)] public int searchNodesPerStep = 192;
    [SerializeField] private string pathStatus = "Ready";
    [SerializeField] private int lastExpandedNodes;

    private readonly Dictionary<long, SearchNode> searchNodes = new Dictionary<long, SearchNode>();
    private readonly List<SearchNode> activeNodes = new List<SearchNode>();
    private readonly Stack<SearchNode> nodePool = new Stack<SearchNode>();

    private IEnumerator<List<Vector2>> pendingSearch;
    private Vector2 pendingStart, pendingGoal;
    private float pendingArrival, pendingCellSize, pendingBodyRadius;
    private Vector2 pendingMapCenter, pendingMapSize;
    private int pendingMask;
    private bool pendingPartial;

    private Rigidbody2D rb;
    private Collider2D bodyCollider;
    private float currentSpeed;
    private float nextRepath;
    private bool wasOnScreen;
    private bool restartFromZero;
    private List<Vector2> path = new List<Vector2>();
    private int waypoint;

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

        if (viewCamera == null)
            viewCamera = Camera.main;
        if (viewCamera == null)
            viewCamera = FindAnyObjectByType<Camera>();
    }

    void OnEnable()
    {
        CancelSearch();
        currentSpeed = Mathf.Clamp(moveSpeed, 0f, maxSpeed);
        nextRepath = 0f;
        wasOnScreen = false;
        restartFromZero = false;
        path.Clear();
        waypoint = 0;
        pathStatus = "Ready";
        lastExpandedNodes = 0;
    }

    void FixedUpdate()
    {
        if (player == null)
        {
            CancelSearch();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // 被手电照到：不逃跑，原地停住并把速度清零，等光移开后重新加速。
        if (flashlight != null && flashlight.IsIlluminating(rb.position))
        {
            currentSpeed = 0f;
            restartFromZero = true;
            ResetPath();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // 屏幕外保持最高速靠近；从屏幕边缘进入画面后，从初始速度重新加速。
        // 如果刚被光停住过，则从 0 重新加速。
        bool onScreen = IsOnScreen();
        if (restartFromZero)
        {
            currentSpeed = Mathf.MoveTowards(
                currentSpeed, maxSpeed, Mathf.Max(0f, acceleration) * Time.fixedDeltaTime);
            if (onScreen)
                restartFromZero = false;
        }
        else if (!onScreen)
        {
            currentSpeed = maxSpeed;
        }
        else
        {
            if (!wasOnScreen)
                currentSpeed = Mathf.Clamp(moveSpeed, 0f, maxSpeed);

            currentSpeed = Mathf.MoveTowards(
                currentSpeed, maxSpeed, Mathf.Max(0f, acceleration) * Time.fixedDeltaTime);
        }
        wasOnScreen = onScreen;

        Vector2 target = player.position;
        float distance = Vector2.Distance(rb.position, target);
        if (distance <= Mathf.Max(0f, stopDistance) &&
            Physics2D.Linecast(rb.position, target, obstacleLayers).collider == null)
        {
            ResetPath();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // 没有墙挡路时直接追赶，避免不必要的格子移动。
        if (SegmentClear(rb.position, target))
        {
            CancelSearch();
            path.Clear();
            waypoint = 0;
            MoveTowards(target, distance - Mathf.Max(0f, stopDistance), currentSpeed);
            nextRepath = 0f;
            return;
        }

        if (Time.fixedTime >= nextRepath)
        {
            path = FindPath(rb.position, target);
            waypoint = 0;
            nextRepath = Time.fixedTime + (pendingSearch != null ? 0f : Mathf.Max(0.1f, repathInterval));
        }

        FollowPath(currentSpeed, float.PositiveInfinity);
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
        while (waypoint + 1 < path.Count && shortcutChecks++ < 12 && SegmentClear(rb.position, path[waypoint + 1]))
            waypoint++;

        if (!SegmentClearWithRadius(rb.position, path[waypoint], BodyRadius))
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
        return SearchSparse(start, goal, arrivalRadius, true);
    }

    // 稀疏节点：地图大小只定义边界，不为未搜索的格子分配数组。
    private sealed class SearchNode
    {
        public int x, y, index;
        public Vector2 position;
        public float cost, wallPenalty;
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
        node.wallPenalty = 0f;
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

    List<Vector2> TracePath(SearchNode end)
    {
        List<Vector2> result = new List<Vector2>();
        for (SearchNode node = end; node != null && node.parent != null; node = node.parent)
            result.Add(node.position);
        result.Reverse();
        return result;
    }

    List<Vector2> SearchSparse(Vector2 start, Vector2 goal, float arrivalRadius, bool allowPartial)
    {
        bool same = pendingSearch != null &&
            Vector2.Distance(start, pendingStart) < 0.05f &&
            Vector2.Distance(goal, pendingGoal) < 0.05f &&
            arrivalRadius == pendingArrival && allowPartial == pendingPartial &&
            cellSize == pendingCellSize &&
            Vector2.Distance(mapCenter, pendingMapCenter) < 0.001f &&
            Vector2.Distance(mapSize, pendingMapSize) < 0.001f &&
            obstacleLayers.value == pendingMask && Mathf.Abs(BodyRadius - pendingBodyRadius) < 0.001f;
        if (!same)
        {
            CancelSearch();
            pendingStart = start;
            pendingGoal = goal;
            pendingArrival = arrivalRadius;
            pendingPartial = allowPartial;
            pendingCellSize = cellSize;
            pendingMapCenter = mapCenter;
            pendingMapSize = mapSize;
            pendingMask = obstacleLayers.value;
            pendingBodyRadius = BodyRadius;
            pendingSearch = SearchSparseSteps(start, goal, arrivalRadius, allowPartial);
        }

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
        CancelSearch();
        return result;
    }

    IEnumerator<List<Vector2>> SearchSparseSteps(Vector2 start, Vector2 goal, float arrivalRadius, bool allowPartial)
    {
        ClearSearch();
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
        MinHeap open = new MinHeap();
        float firstDistance = Vector2.Distance(start, goal);
        open.Push(first.index, Mathf.Max(0f, firstDistance - arrivalRadius));
        SearchNode nearest = first, nearestVisible = null;
        float nearestDistance = firstDistance;
        float visibleDistance = float.PositiveInfinity;
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
            float distance = Vector2.Distance(current.position, goal);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = current;
            }

            bool needsVisibility = distance <= arrivalRadius || distance < visibleDistance;
            bool visible = needsVisibility && Physics2D.Linecast(current.position, goal, obstacleLayers).collider == null;
            if (visible && distance < visibleDistance)
            {
                visibleDistance = distance;
                nearestVisible = current;
            }
            if (visible && distance <= arrivalRadius)
            {
                pathStatus = "Complete route";
                yield return TracePath(current); yield break;
            }

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = current.x + dx, ny = current.y + dy;
                Vector2 position = origin + new Vector2((nx + 0.5f) * step, (ny + 0.5f) * step);
                if (!InMap(position)) continue;

                SearchNode next = GetNode(nx, ny, origin, step);
                if (next.closed) continue;
                if (next.walkable == 0)
                {
                    next.walkable = (sbyte)(Physics2D.OverlapCircle(next.position, body, obstacleLayers) == null ? 1 : -1);
                    next.wallPenalty = Physics2D.OverlapCircle(next.position, preferred, obstacleLayers) != null ? 4f : 0f;
                }
                if (next.walkable < 0) continue;

                float cost = current.cost + Vector2.Distance(current.position, next.position) * (1f + next.wallPenalty);
                if (cost >= next.cost) continue;
                if (!SegmentClearWithRadius(current.position, next.position, body)) continue;

                next.cost = cost;
                next.parent = current;
                float heuristic = Mathf.Max(0f, Vector2.Distance(next.position, goal) - arrivalRadius);
                open.Push(next.index, cost + heuristic);
            }
        }

        if (!allowPartial)
        {
            pathStatus = open.Count > 0 ? "Search budget reached; waiting safely" : "No route";
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

    bool InMap(Vector2 point)
    {
        Vector2 offset = point - mapCenter;
        return Mathf.Abs(offset.x) < mapSize.x * 0.5f && Mathf.Abs(offset.y) < mapSize.y * 0.5f;
    }

    bool IsOnScreen()
    {
        if (viewCamera == null)
            return true;

        Vector3 viewport = viewCamera.WorldToViewportPoint(transform.position);
        return viewport.z > 0f &&
            viewport.x >= 0f && viewport.x <= 1f &&
            viewport.y >= 0f && viewport.y <= 1f;
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
