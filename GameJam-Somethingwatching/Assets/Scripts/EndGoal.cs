using UnityEngine;
using UnityEngine.Rendering.Universal;

public class EndGoal : MonoBehaviour
{
    public Flashlight2D flashlight;
    public Light2D freeformLight;

    private bool reached;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (reached)
            return;

        PlayerMovement movement =
            other.GetComponentInParent<PlayerMovement>();

        if (movement == null)
            return;

        reached = true;
        movement.enabled = false;

        Rigidbody2D body = movement.GetComponent<Rigidbody2D>();

        if (body != null)
        {
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
        }

        if (flashlight == null)
            flashlight = movement.GetComponentInChildren<Flashlight2D>();

        if (flashlight != null)
        {
            flashlight.enabled = false;

            Light2D light = flashlight.GetComponent<Light2D>();

            if (light != null)
                light.enabled = false;
        }

        if (freeformLight != null)
            freeformLight.enabled = false;
    }
}