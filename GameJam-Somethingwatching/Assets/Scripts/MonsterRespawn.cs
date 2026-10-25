using UnityEngine;

/// <summary>
/// 怪物离开屏幕后的隐藏与重生：
/// - EnemyChase 在逃离屏幕后调用 Despawn()；
/// - 隐藏/停用怪物 5 秒；
/// - 在玩家当前屏幕外、地图内且不与墙体重叠的随机位置重生。
/// </summary>
[DisallowMultipleComponent]
public class MonsterRespawn : MonoBehaviour
{
    [Header("Respawn / 重生")]
    [Tooltip("怪物消失的持续时间（秒，使用不受 timeScale 影响的时间）。")]
    [Min(0f)] public float respawnDelay = 5f;
    [Tooltip("重生点超出屏幕边缘的世界距离。")]
    [Min(0f)] public float spawnMargin = 1.5f;
    [Tooltip("重生点与墙体/障碍物的最小间距。")]
    [Min(0.01f)] public float spawnClearance = 0.6f;
    [Tooltip("判断墙体/障碍物的层；留空时使用 EnemyChase 的 obstacleLayers。")]
    public LayerMask obstacleLayers;
    [Tooltip("用于判断屏幕和寻找重生点的相机；留空自动使用 Camera.main。")]
    public Camera viewCamera;

    [Header("引用 / References")]
    public EnemyChase chase;
    public MonsterSpeedDirector speedDirector;
    public MonsterEffects effects;

    private Rigidbody2D body;
    private Collider2D bodyCollider;
    private SpriteRenderer[] renderers;
    private bool despawned;
    private float respawnTime;

    private void Awake()
    {
        if (chase == null)
            chase = GetComponent<EnemyChase>();
        if (speedDirector == null)
            speedDirector = GetComponent<MonsterSpeedDirector>();
        if (effects == null)
            effects = GetComponent<MonsterEffects>();

        body = GetComponent<Rigidbody2D>();
        bodyCollider = GetComponent<Collider2D>();
        renderers = GetComponentsInChildren<SpriteRenderer>(true);

        if (viewCamera == null)
            viewCamera = Camera.main;
        if (viewCamera == null)
            viewCamera = FindAnyObjectByType<Camera>();

        if (obstacleLayers.value == 0 && chase != null)
            obstacleLayers = chase.obstacleLayers;
    }

    /// <summary>由 EnemyChase 在怪物逃离屏幕后调用。</summary>
    public void Despawn()
    {
        if (despawned)
            return;

        despawned = true;
        respawnTime = Time.unscaledTime + Mathf.Max(0f, respawnDelay);

        if (body != null)
        {
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
            body.simulated = false;
        }

        if (bodyCollider != null)
            bodyCollider.enabled = false;

        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = false;

        AudioSource audio = GetComponent<AudioSource>();
        if (audio != null)
            audio.Stop();

        // 停掉 AI 和表现组件，MonsterRespawn 自己继续计时。
        if (chase != null)
            chase.enabled = false;
        if (speedDirector != null)
            speedDirector.enabled = false;
        if (effects != null)
            effects.enabled = false;
    }

    private void Update()
    {
        if (!despawned || Time.unscaledTime < respawnTime)
            return;

        // 重新生成后必须能完整寻路到玩家；否则立刻换一个点重新生成。
        const int MaxSpawnAttempts = 8;
        for (int attempt = 0; attempt < MaxSpawnAttempts; attempt++)
        {
            Vector2 spawn;
            if (!TryFindSpawnPoint(out spawn))
                continue;

            if (chase != null && !chase.CanPathToPlayerFrom(spawn))
                continue;

            RespawnAt(spawn);
            return;
        }

        // 当前这几条屏幕边都没有可达点，稍后继续尝试。
        respawnTime = Time.unscaledTime + 0.5f;
    }

    private void RespawnAt(Vector2 spawn)
    {
        transform.position = new Vector3(spawn.x, spawn.y, transform.position.z);

        if (body != null)
        {
            body.simulated = true;
            body.position = spawn;
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
        }

        if (bodyCollider != null)
            bodyCollider.enabled = true;

        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = true;

        if (chase != null)
            chase.enabled = true;
        if (speedDirector != null)
            speedDirector.enabled = true;
        if (effects != null)
            effects.enabled = true;

        despawned = false;
    }

    // 在镜头四边外 1~2 个单位的范围内随机取点；地图边缘/墙体不合格时重试。
    private bool TryFindSpawnPoint(out Vector2 point)
    {
        point = transform.position;
        if (viewCamera == null)
            return false;

        Vector2 camera = viewCamera.transform.position;
        float halfHeight = viewCamera.orthographicSize;
        float halfWidth = halfHeight * viewCamera.aspect;
        float margin = Mathf.Max(0f, spawnMargin);
        float clearance = Mathf.Max(0.01f, spawnClearance);

        Vector2 mapMin = chase != null
            ? chase.mapCenter - chase.mapSize * 0.5f
            : new Vector2(-10000f, -10000f);
        Vector2 mapMax = chase != null
            ? chase.mapCenter + chase.mapSize * 0.5f
            : new Vector2(10000f, 10000f);

        for (int attempt = 0; attempt < 80; attempt++)
        {
            Vector2 candidate;
            switch (Random.Range(0, 4))
            {
                case 0:
                    candidate = new Vector2(
                        Random.Range(camera.x - halfWidth, camera.x + halfWidth),
                        camera.y + halfHeight + margin);
                    break;
                case 1:
                    candidate = new Vector2(
                        Random.Range(camera.x - halfWidth, camera.x + halfWidth),
                        camera.y - halfHeight - margin);
                    break;
                case 2:
                    candidate = new Vector2(
                        camera.x - halfWidth - margin,
                        Random.Range(camera.y - halfHeight, camera.y + halfHeight));
                    break;
                default:
                    candidate = new Vector2(
                        camera.x + halfWidth + margin,
                        Random.Range(camera.y - halfHeight, camera.y + halfHeight));
                    break;
            }

            if (!InMap(candidate, mapMin, mapMax, clearance))
                continue;
            if (IsOnScreen(candidate))
                continue;
            if (Physics2D.OverlapCircle(candidate, clearance, obstacleLayers) != null)
                continue;

            point = candidate;
            return true;
        }

        // 镜头附近都不合法时，在地图内随机找任意屏幕外位置。
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Vector2 candidate = new Vector2(
                Random.Range(mapMin.x + clearance, mapMax.x - clearance),
                Random.Range(mapMin.y + clearance, mapMax.y - clearance));

            if (IsOnScreen(candidate))
                continue;
            if (Physics2D.OverlapCircle(candidate, clearance, obstacleLayers) != null)
                continue;

            point = candidate;
            return true;
        }

        return false;
    }

    private static bool InMap(Vector2 point, Vector2 mapMin, Vector2 mapMax, float clearance)
    {
        return point.x > mapMin.x + clearance && point.x < mapMax.x - clearance &&
            point.y > mapMin.y + clearance && point.y < mapMax.y - clearance;
    }

    private bool IsOnScreen(Vector2 point)
    {
        if (viewCamera == null)
            return false;

        Vector3 viewport = viewCamera.WorldToViewportPoint(point);
        return viewport.z > 0f &&
            viewport.x >= 0f && viewport.x <= 1f &&
            viewport.y >= 0f && viewport.y <= 1f;
    }
}
