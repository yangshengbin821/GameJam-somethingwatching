using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(Light2D))]
public class Flashlight2D : MonoBehaviour
{
    [Min(0.1f)]
    public float range = 6f;

    [Range(1f, 179f)]
    public float coneAngle = 60f;

    // 我快疯了
    public LayerMask obstacleLayers;

    [Min(0f)]
    [Tooltip("Flashlight brightness before the CRT filter.")]
    public float lightIntensity = 1.5f;

    private Light2D lamp;

    void Awake()
    {
        lamp = GetComponent<Light2D>();
        if (obstacleLayers.value == 0)
            obstacleLayers = LayerMask.GetMask("Obstacle");
        ApplyLightSettings();
    }

    void LateUpdate()
    {
        ApplyLightSettings();
    }

    void ApplyLightSettings()
    {
        if (lamp == null)
            lamp = GetComponent<Light2D>();

        lamp.pointLightOuterRadius = range;
        lamp.pointLightInnerRadius = range * 0.5f;
        lamp.pointLightOuterAngle = coneAngle;
        lamp.pointLightInnerAngle = coneAngle * 0.8f;

        
        lamp.shadowsEnabled = true;
        lamp.shadowIntensity = 1f;

        //妈妈
        lamp.intensity = IsEmitterBlocked() ? 0f : lightIntensity;
    }

    bool IsEmitterBlocked()
    {
        Vector2 origin = transform.position;
        if (Physics2D.OverlapPoint(origin, obstacleLayers) != null)
            return true;

        if (transform.parent != null)
        {
            Vector2 playerPosition = transform.parent.position;
            if (Physics2D.Linecast(playerPosition, origin, obstacleLayers).collider != null)
                return true;
        }
        return false;
    }

    // 怪物中心在扇形内，且光源到怪物之间没有墙，才算被照到。
    public bool IsIlluminating(Vector2 targetPosition)
    {
        if (lamp == null)
            lamp = GetComponent<Light2D>();

        if (!isActiveAndEnabled || lamp == null || !lamp.isActiveAndEnabled)
            return false;

        Vector2 origin = transform.position;
        Vector2 direction = targetPosition - origin;
        if (direction.sqrMagnitude > range * range)
            return false;

        if (direction.sqrMagnitude > 0.0001f &&
            Vector2.Angle((Vector2)transform.up, direction) > coneAngle * 0.5f)
            return false;

        if (IsEmitterBlocked())
            return false;

        return Physics2D.Linecast(origin, targetPosition, obstacleLayers).collider == null;
    }
}

