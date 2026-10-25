using UnityEngine;

/// <summary>
/// 怪物表现层：手电照到怪物时尖叫；怪物接触玩家时尖叫并显示 jumpscare 图片。
/// 与 EnemyChase 分离，替换寻路/行动脚本时这些表现不会丢失。
/// </summary>
[DisallowMultipleComponent]
public class MonsterEffects : MonoBehaviour
{
    [Header("尖叫 / Scream")]
    [Tooltip("被手电照到、以及碰到玩家时播放的尖叫声。")]
    public AudioClip screamClip;
    [Range(0f, 1f)] public float screamVolume = 1f;

    [Header("接触玩家 / Jumpscare")]
    [Tooltip("怪物碰到玩家时全屏显示的图片。")]
    public Texture2D jumpscareImage;
    [Tooltip("图片显示时长（秒，使用不受 timeScale 影响的时间）。")]
    [Min(0f)] public float jumpscareDuration = 3f;

    [Header("引用 / References")]
    [Tooltip("玩家手电；留空时自动寻找。")]
    public Flashlight2D flashlight;

    private AudioSource audioSource;
    private bool wasIlluminated;
    private float jumpscareEndTime = -1f;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = false;

        if (flashlight == null)
            flashlight = FindAnyObjectByType<Flashlight2D>();
    }

    private void OnEnable()
    {
        wasIlluminated = false;
        jumpscareEndTime = -1f;
    }

    private void FixedUpdate()
    {
        bool illuminated = flashlight != null && flashlight.IsIlluminating(transform.position);
        if (illuminated && !wasIlluminated)
            PlayScream();

        wasIlluminated = illuminated;
    }

    // 怪物与玩家接触时触发 jumpscare：同一时间只显示一张图、只尖叫一次。
    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (Time.unscaledTime < jumpscareEndTime)
            return;

        if (collision.collider.GetComponentInParent<PlayerMovement>() == null)
            return;

        jumpscareEndTime = Time.unscaledTime + Mathf.Max(0f, jumpscareDuration);
        PlayScream();
    }

    private void PlayScream()
    {
        if (screamClip == null || audioSource == null)
            return;

        audioSource.Stop();
        audioSource.PlayOneShot(screamClip, Mathf.Clamp01(screamVolume));
    }

    // 全屏显示 jumpscare 图片：黑底 + 保持原图比例居中。
    private void OnGUI()
    {
        if (jumpscareImage == null || Time.unscaledTime >= jumpscareEndTime)
            return;

        Color previousColor = GUI.color;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = previousColor;

        float imageAspect = (float)jumpscareImage.width / jumpscareImage.height;
        float screenAspect = (float)Screen.width / Screen.height;

        Rect rect;
        if (imageAspect > screenAspect)
        {
            float height = Screen.width / imageAspect;
            rect = new Rect(0f, (Screen.height - height) * 0.5f, Screen.width, height);
        }
        else
        {
            float width = Screen.height * imageAspect;
            rect = new Rect((Screen.width - width) * 0.5f, 0f, width, Screen.height);
        }

        GUI.DrawTexture(rect, jumpscareImage, ScaleMode.StretchToFill, true);
    }
}
