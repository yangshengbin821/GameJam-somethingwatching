using UnityEngine;

/// <summary>
/// 追击速度控制器：不修改 EnemyChase 的寻路核心，只驱动它的 moveSpeed。
/// 屏幕外全速追赶；刚进入屏幕降到入屏速度；之后逐渐加速到上限；
/// 被手电照到后重置，等逃跑结束再次从入屏速度开始。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(EnemyChase))]
public class MonsterSpeedDirector : MonoBehaviour
{
    [Header("追击速度 / Chase Speed")]
    [Tooltip("怪物在屏幕外时的追击速度。")]
    [Min(0f)] public float offscreenSpeed = 15f;
    [Tooltip("怪物刚进入屏幕时的追击速度。")]
    [Min(0f)] public float onScreenStartSpeed = 3f;
    [Tooltip("在屏幕内逐渐加速到的最高速度。")]
    [Min(0f)] public float onScreenMaxSpeed = 6f;
    [Tooltip("在屏幕内每秒增加的速度。")]
    [Min(0f)] public float onScreenAcceleration = 1.5f;

    [Header("引用 / References")]
    [Tooltip("留空时自动获取本物体上的 EnemyChase。")]
    public EnemyChase chase;
    [Tooltip("玩家手电；留空时自动寻找。")]
    public Flashlight2D flashlight;
    [Tooltip("用于判断屏幕内外的相机；留空时使用 Camera.main。")]
    public Camera viewCamera;

    private float chaseSpeed;
    private bool wasOnScreen;

    private void Awake()
    {
        if (chase == null)
            chase = GetComponent<EnemyChase>();
        if (flashlight == null)
            flashlight = FindAnyObjectByType<Flashlight2D>();
        if (viewCamera == null)
            viewCamera = Camera.main;
        if (viewCamera == null)
            viewCamera = FindAnyObjectByType<Camera>();

        chaseSpeed = Mathf.Max(0f, onScreenStartSpeed);
    }

    private void OnEnable()
    {
        chaseSpeed = Mathf.Max(0f, onScreenStartSpeed);
        wasOnScreen = false;
    }

    private void FixedUpdate()
    {
        if (chase == null)
            return;

        bool illuminated = flashlight != null && flashlight.IsIlluminating(transform.position);
        UpdateSpeed(illuminated);
        // EnemyChase 内部所有非逃跑移动都读取 moveSpeed。
        chase.moveSpeed = chaseSpeed;
    }

    // 屏幕外全速；入屏重置为 3 并逐渐加速；被照到后重置，等逃跑结束重新开始。
    private void UpdateSpeed(bool illuminated)
    {
        if (illuminated)
        {
            ResetSpeed();
            return;
        }

        if (!IsOnScreen())
        {
            chaseSpeed = Mathf.Max(0f, offscreenSpeed);
            wasOnScreen = false;
            return;
        }

        if (!wasOnScreen)
        {
            chaseSpeed = Mathf.Max(0f, onScreenStartSpeed);
            wasOnScreen = true;
            return;
        }

        chaseSpeed = Mathf.MoveTowards(
            chaseSpeed,
            Mathf.Max(0f, onScreenMaxSpeed),
            Mathf.Max(0f, onScreenAcceleration) * Time.fixedDeltaTime);
    }

    private void ResetSpeed()
    {
        chaseSpeed = Mathf.Max(0f, onScreenStartSpeed);
        wasOnScreen = false;
    }

    private bool IsOnScreen()
    {
        if (viewCamera == null)
            return true;

        Vector3 viewport = viewCamera.WorldToViewportPoint(transform.position);
        return viewport.z > 0f &&
            viewport.x >= 0f && viewport.x <= 1f &&
            viewport.y >= 0f && viewport.y <= 1f;
    }
}
