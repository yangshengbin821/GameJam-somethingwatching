using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelGoal : MonoBehaviour
{
    private bool reached;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (reached)
            return;

        PlayerMovement movement = other.GetComponentInParent<PlayerMovement>();

        if (movement == null)
            return;

        reached = true;
        Time.timeScale = 1f;
        SceneManager.LoadScene("Death");
    }
}