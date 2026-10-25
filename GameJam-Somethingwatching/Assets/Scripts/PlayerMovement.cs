using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMovement : MonoBehaviour
{
    [Header("移动速度")]
    [Min(0f)]
    public float moveSpeed = 5f;

    [Header("转向速度：每秒旋转多少度")]
    [Min(0f)]
    public float turnSpeed = 180f;

    [Header("图片朝向修正")]
    [Tooltip("图片默认朝上填 -90，默认朝右填 0")]
    public float rotationOffset = -90f;

    private Rigidbody2D rb;
    private bool forwardPressed;
    private float turnInput;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void Update()
    {
        forwardPressed = false;
        turnInput = 0f;

        Keyboard keyboard = Keyboard.current;

        if (keyboard != null && Application.isFocused)
        {
            forwardPressed = keyboard.wKey.isPressed;

            if (keyboard.aKey.isPressed)
                turnInput += 1f;

            if (keyboard.dKey.isPressed)
                turnInput -= 1f;
        }

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetPlayerMoving(
                forwardPressed && moveSpeed > 0f && Time.timeScale > 0f
            );
        }
    }

    void FixedUpdate()
    {
        if (Time.timeScale == 0f)
            return;

        float nextRotation =
            rb.rotation +
            turnInput * turnSpeed * Time.fixedDeltaTime;

        rb.MoveRotation(nextRotation);

        if (forwardPressed)
        {
            // 根据角色朝向计算前进方向。
            float angle =
                (nextRotation - rotationOffset) * Mathf.Deg2Rad;

            Vector2 forward = new Vector2(
                Mathf.Cos(angle),
                Mathf.Sin(angle));

            rb.linearVelocity = forward * moveSpeed;
        }
        else
        {
            rb.linearVelocity = Vector2.zero;
        }
    }

    void OnDisable()
    {
        forwardPressed = false;
        turnInput = 0f;

        if (rb != null)
        {
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }
        if (AudioManager.Instance != null)
            AudioManager.Instance.SetPlayerMoving(false);
    }
}