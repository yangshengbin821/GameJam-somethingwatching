using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChaseNoRespawn : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    public Flashlight2D flashlight;
    public Camera viewCamera;

    [Header("Chase Speed")]
    [Min(0f)] public float moveSpeed = 3f;
    [Min(0f)] public float acceleration = 0.25f;
    [Min(0f)] public float maxSpeed = 5.5f;

    [Header("Retreat")]
    [Min(0f)] public float retreatSpeed = 7f;
    [Min(0f)] public float recoveryDuration = 2.5f;

    private Rigidbody2D rb;
    private float currentSpeed;
    private float recoveryTimer;
    private bool wasOnScreen;
    private bool wasIlluminated;
    private bool resumeChase;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();

        if (viewCamera == null)
            viewCamera = Camera.main;
    }

    void OnEnable()
    {
        currentSpeed = Mathf.Clamp(moveSpeed, 0f, maxSpeed);
        recoveryTimer = 0f;
        wasOnScreen = false;
        wasIlluminated = false;
        resumeChase = false;
    }

    void FixedUpdate()
    {
        if (player == null || !player.gameObject.activeInHierarchy)
        {
            rb.linearVelocity = Vector2.zero;
            return;
        }

        bool onScreen = IsOnScreen();

        bool illuminated = flashlight != null &&
            flashlight.IsIlluminating(rb.position);

        if (illuminated)
        {
            Vector2 retreatDirection =
                rb.position - (Vector2)flashlight.transform.position;

            if (retreatDirection.sqrMagnitude < 0.0001f)
                retreatDirection = flashlight.transform.up;

            rb.WakeUp();
            rb.linearVelocity =
                retreatDirection.normalized * retreatSpeed;

            currentSpeed = 0f;
            recoveryTimer = 0f;
            wasIlluminated = true;
            wasOnScreen = onScreen;
            resumeChase = true;
            return;
        }

        if (wasIlluminated)
        {
            wasIlluminated = false;
            recoveryTimer = recoveryDuration;
        }

        if (recoveryTimer > 0f)
        {
            recoveryTimer = Mathf.Max(
                0f, recoveryTimer - Time.fixedDeltaTime
            );

            rb.linearVelocity = Vector2.zero;
            wasOnScreen = onScreen;
            return;
        }

        float speedLimit = Mathf.Max(0f, maxSpeed);

        if (!onScreen)
        {
            currentSpeed = speedLimit;
        }
        else
        {
            if (resumeChase || !wasOnScreen)
                currentSpeed = Mathf.Clamp(moveSpeed, 0f, speedLimit);

            currentSpeed = Mathf.MoveTowards(
                currentSpeed,
                speedLimit,
                Mathf.Max(0f, acceleration) * Time.fixedDeltaTime
            );
        }

        resumeChase = false;
        wasOnScreen = onScreen;

        Vector2 direction = (Vector2)player.position - rb.position;
        float distance = direction.magnitude;

        if (distance <= 0.001f)
        {
            rb.linearVelocity = Vector2.zero;
            return;
        }

        float speed = Mathf.Min(
            currentSpeed,
            distance / Time.fixedDeltaTime
        );

        rb.WakeUp();
        rb.linearVelocity = direction.normalized * speed;
    }

    bool IsOnScreen()
    {
        if (viewCamera == null)
            return true;

        Vector3 viewport =
            viewCamera.WorldToViewportPoint(transform.position);

        return viewport.z > 0f &&
            viewport.x >= 0f && viewport.x <= 1f &&
            viewport.y >= 0f && viewport.y <= 1f;
    }

    void OnDisable()
    {
        if (rb != null)
            rb.linearVelocity = Vector2.zero;
    }
}