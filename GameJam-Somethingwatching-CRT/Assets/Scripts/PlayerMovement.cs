using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMovement : MonoBehaviour
{
    public float moveSpeed = 5f;

    // 角色图片默认朝右填 0，默认朝上填 -90
    public float rotationOffset = 0f;

    private Rigidbody2D rb;
    private Camera mainCamera;

    private Vector2 moveInput;
    private float lookAngle;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        mainCamera = Camera.main;
        lookAngle = rb.rotation;
    }

    void Update()
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

        // 读取鼠标位置
        Mouse mouse = Mouse.current;

        if (mouse == null || mainCamera == null)
            return;

        Vector2 mouseScreenPosition = mouse.position.ReadValue();

        // 将屏幕位置转换到角色所在的 XY 平面
        Ray ray = mainCamera.ScreenPointToRay(
            new Vector3(mouseScreenPosition.x, mouseScreenPosition.y, 0f)
        );

        Plane playerPlane = new Plane(Vector3.forward, transform.position);

        if (playerPlane.Raycast(ray, out float distance))
        {
            Vector3 mouseWorldPosition = ray.GetPoint(distance);
            Vector2 lookDirection =
                (Vector2)mouseWorldPosition - rb.position;

            // 鼠标与角色重合时，保留原来的朝向
            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                lookAngle =
                    Mathf.Atan2(lookDirection.y, lookDirection.x)
                    * Mathf.Rad2Deg
                    + rotationOffset;
            }
        }
    }

    void FixedUpdate()
    {
        rb.linearVelocity = moveInput * moveSpeed;
        rb.MoveRotation(lookAngle);
    }

    void OnDisable()
    {
        if (rb != null)
            rb.linearVelocity = Vector2.zero;
    }
}