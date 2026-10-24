using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMovement : MonoBehaviour
{
    public float moveSpeed = 5f;

    // 角色图片默认朝右填 0，默认朝上填 -90
    public float rotationOffset = 0f;

    public enum MapDirection
    {
        Right,
        Left,
        Up,
        Down
    }

    [Header("走到地图中途时切换音乐")]
    public MapDirection mapDirection = MapDirection.Right;

    [Tooltip("向左或向右走填分界位置的 X；向上或向下走填 Y")]
    public float phase2Position = 10f;

    private Rigidbody2D rb;
    private Camera mainCamera;

    private Vector2 moveInput;
    private Vector2 previousPosition;
    private float lookAngle;
    private bool phase2Started;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        mainCamera = Camera.main;
        lookAngle = rb.rotation;
        previousPosition = rb.position;
    }

    private void Update()
    {
        // 读取 WASD
        moveInput = Vector2.zero;
        Keyboard keyboard = Keyboard.current;

        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed) moveInput.y += 1f;
            if (keyboard.sKey.isPressed) moveInput.y -= 1f;
            if (keyboard.aKey.isPressed) moveInput.x -= 1f;
            if (keyboard.dKey.isPressed) moveInput.x += 1f;
        }

        moveInput = moveInput.normalized;

        // 松开按键或暂停时，立即停止脚步声
        if (moveInput == Vector2.zero || Time.timeScale <= 0f)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.SetPlayerMoving(false);
        }

       // CheckPhase2Music();

        // 读取鼠标位置
        Mouse mouse = Mouse.current;

        if (mouse == null || mainCamera == null)
            return;

        Vector2 mouseScreenPosition = mouse.position.ReadValue();

        Ray ray = mainCamera.ScreenPointToRay(
            new Vector3(
                mouseScreenPosition.x,
                mouseScreenPosition.y,
                0f)
        );

        Plane playerPlane =
            new Plane(Vector3.forward, transform.position);

        if (playerPlane.Raycast(ray, out float distance))
        {
            Vector3 mouseWorldPosition = ray.GetPoint(distance);

            Vector2 lookDirection =
                (Vector2)mouseWorldPosition - rb.position;

            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                lookAngle =
                    Mathf.Atan2(lookDirection.y, lookDirection.x)
                    * Mathf.Rad2Deg
                    + rotationOffset;
            }
        }
    }

    private void FixedUpdate()
    {
        // 检查上一次物理更新后，玩家是否真的移动了
        bool actuallyMoved =
            (rb.position - previousPosition).sqrMagnitude > 0.000001f;

        previousPosition = rb.position;

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetPlayerMoving(
                moveInput.sqrMagnitude > 0f && actuallyMoved);
        }

        rb.linearVelocity = moveInput * moveSpeed;
        rb.MoveRotation(lookAngle);
    }

    private void CheckPhase2Music()
    {
        if (phase2Started || Time.timeScale <= 0f)
            return;

        bool reached = false;

        switch (mapDirection)
        {
            case MapDirection.Right:
                reached = rb.position.x >= phase2Position;
                break;

            case MapDirection.Left:
                reached = rb.position.x <= phase2Position;
                break;

            case MapDirection.Up:
                reached = rb.position.y >= phase2Position;
                break;

            case MapDirection.Down:
                reached = rb.position.y <= phase2Position;
                break;
        }

        if (!reached || AudioManager.Instance == null)
            return;

        if (AudioManager.Instance.phase2Music == null)
            return;

        AudioManager.Instance.PlayPhase2Music();
        phase2Started = true;
    }

    private void OnEnable()
    {
        if (rb != null)
            previousPosition = rb.position;
    }

    private void OnDisable()
    {
        moveInput = Vector2.zero;

        if (rb != null)
            rb.linearVelocity = Vector2.zero;

        if (AudioManager.Instance != null)
            AudioManager.Instance.SetPlayerMoving(false);
    }
}