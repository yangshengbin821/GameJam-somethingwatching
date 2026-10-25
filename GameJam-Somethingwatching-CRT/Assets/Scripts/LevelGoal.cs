using UnityEngine;

/// <summary>
/// 终点触发器：玩家进入楼梯区域的 Trigger 后停止玩家移动并显示通关文字。
/// </summary>
public class LevelGoal : MonoBehaviour
{
    private bool reached;
    private GUIStyle style;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (reached)
            return;

        PlayerMovement movement = other.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        reached = true;
        movement.enabled = false;

        Rigidbody2D body = other.attachedRigidbody;
        if (body != null)
            body.linearVelocity = Vector2.zero;

        Debug.Log("LEVEL COMPLETE - 到达终点");
    }

    private void OnGUI()
    {
        if (!reached)
            return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 42;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;
        }

        Rect rect = new Rect(0f, Screen.height * 0.42f, Screen.width, 90f);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(rect, "LEVEL COMPLETE", style);
    }
}
