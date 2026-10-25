using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class MonsterEffects : MonoBehaviour
{
    public string deathSceneName = "Death";

    private bool triggered;

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (triggered)
            return;

        if (collision.collider.GetComponentInParent<PlayerMovement>() == null)
            return;

        triggered = true;
        Time.timeScale = 1f;
        SceneManager.LoadScene(deathSceneName);
    }
}