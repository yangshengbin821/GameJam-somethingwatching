using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public Transform player;

    private float cameraZ;

    void Awake()
    {
        cameraZ = transform.position.z;
    }

    void LateUpdate()
    {
        if (player == null)
            return;

        transform.position = new Vector3(
            player.position.x,
            player.position.y,
            cameraZ
        );
    }
}