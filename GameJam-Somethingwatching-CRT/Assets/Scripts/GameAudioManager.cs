using UnityEngine;

[DisallowMultipleComponent]
public class GameAudioManager : MonoBehaviour
{
    [Header("角色")]
    public Transform player;
    public Transform monster;
    public Flashlight2D flashlight;

    [Header("玩家脚步声")]
    public AudioClip playerFootstep;
    [Range(0f, 1f)] public float playerFootstepVolume = 0.5f;
    [Min(0.05f)] public float playerStepInterval = 0.35f;

    [Header("怪物脚步声")]
    public AudioClip monsterFootstep;
    [Range(0f, 1f)] public float monsterMaxVolume = 1f;
    [Min(0.05f)] public float monsterStepInterval = 0.4f;

    [Tooltip("距离玩家多近时，怪物声音达到最大音量")]
    [Min(0f)] public float fullVolumeDistance = 1f;

    [Tooltip("超过这个距离，听不到怪物脚步和尖叫")]
    [Min(0.1f)] public float hearingDistance = 12f;

    [Header("怪物被照到时的尖叫")]
    public AudioClip monsterScream;
    [Range(0f, 1f)] public float monsterScreamVolume = 1f;

    [Tooltip("两次怪物尖叫之间的最短间隔，单位秒")]
    [Min(0f)] public float monsterScreamCooldown = 1.5f;

    [Header("玩家被碰到时的惨叫")]
    public AudioClip playerScream;
    [Range(0f, 1f)] public float playerScreamVolume = 1f;

    [Tooltip("两次玩家惨叫之间的最短间隔，单位秒")]
    [Min(0f)] public float playerScreamCooldown = 1.5f;

    [Header("背景音乐：两个插槽")]
    public AudioClip backgroundMusic1;
    public AudioClip backgroundMusic2;
    [Range(0f, 1f)] public float musicVolume = 0.3f;

    [Tooltip("0 = 不播放，1 = 第一首，2 = 第二首")]
    [Range(0, 2)] public int startingMusic = 1;

    [Header("移动检测")]
    [Min(0.001f)] public float movementThreshold = 0.05f;

    private AudioSource playerSource;
    private AudioSource monsterSource;
    private AudioSource musicSource;
    private AudioSource monsterScreamSource;
    private AudioSource playerScreamSource;

    private readonly CharacterTrack playerTrack = new CharacterTrack();
    private readonly CharacterTrack monsterTrack = new CharacterTrack();

    private float playerStepTimer;
    private float monsterStepTimer;
    private float nextMonsterScream;
    private float nextPlayerScream;

    private bool wasIlluminated;
    private bool wasTouching;

    // 保存角色的物理位置和碰撞体。
    private class CharacterTrack
    {
        public Transform target;
        public Rigidbody2D body;
        public Collider2D[] colliders = new Collider2D[0];
        public Vector2 lastPosition;

        public void Bind(Transform value)
        {
            target = value;
            body = value != null
                ? value.GetComponent<Rigidbody2D>()
                : null;

            colliders = value != null
                ? value.GetComponentsInChildren<Collider2D>(true)
                : new Collider2D[0];

            lastPosition = Position;
        }

        public bool Active =>
            target != null && target.gameObject.activeInHierarchy;

        public Vector2 Position
        {
            get
            {
                if (body != null)
                    return body.position;

                return target != null
                    ? (Vector2)target.position
                    : Vector2.zero;
            }
        }
    }

    void Awake()
    {
        playerSource = CreateSource(false);
        monsterSource = CreateSource(false);
        musicSource = CreateSource(true);
        monsterScreamSource = CreateSource(false);
        playerScreamSource = CreateSource(false);

        musicSource.priority = 32;
        monsterScreamSource.priority = 48;
        playerScreamSource.priority = 48;
    }

    AudioSource CreateSource(bool loop)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f;
        source.dopplerLevel = 0f;
        return source;
    }

    void OnEnable()
    {
        playerTrack.Bind(player);
        monsterTrack.Bind(monster);

        playerStepTimer = 0f;
        monsterStepTimer = 0f;
        nextMonsterScream = 0f;
        nextPlayerScream = 0f;
        wasIlluminated = false;
        wasTouching = false;

        UpdateVolumes();
        PlayMusic(startingMusic);
    }

    void FixedUpdate()
    {
        // 支持运行时更换玩家或怪物引用。
        bool changed = false;

        if (playerTrack.target != player)
        {
            playerTrack.Bind(player);
            playerStepTimer = 0f;
            changed = true;
        }

        if (monsterTrack.target != monster)
        {
            monsterTrack.Bind(monster);
            monsterStepTimer = 0f;
            changed = true;
        }

        if (changed)
        {
            wasIlluminated = false;
            wasTouching = false;
        }

        UpdateVolumes();

        bool playerMoving = HasMoved(playerTrack);
        bool monsterMoving = HasMoved(monsterTrack);

        UpdateFootsteps(
            playerSource, playerFootstep,
            playerMoving, playerTrack.Active,
            playerStepInterval, ref playerStepTimer);

        UpdateFootsteps(
            monsterSource, monsterFootstep,
            monsterMoving,
            playerTrack.Active && monsterTrack.Active,
            monsterStepInterval, ref monsterStepTimer);

        CheckScreams();
    }

    void Update()
    {
        UpdateVolumes();
    }

    bool HasMoved(CharacterTrack track)
    {
        Vector2 position = track.Position;
        float distance = Vector2.Distance(position, track.lastPosition);
        track.lastPosition = position;

        return track.Active &&
            distance > Mathf.Max(0.001f, movementThreshold)
            * Time.fixedDeltaTime;
    }

    void UpdateFootsteps(
        AudioSource source,
        AudioClip clip,
        bool moving,
        bool active,
        float interval,
        ref float timer)
    {
        if (!active || clip == null)
        {
            if (source.isPlaying)
                source.Stop();

            timer = 0f;
            return;
        }

        if (Time.timeScale == 0f)
            return;

        timer = Mathf.Max(0f, timer - Time.fixedDeltaTime);

        // 短暂停顿不截断已经响起的脚步。
        if (moving && timer <= 0f)
        {
            source.PlayOneShot(clip);
            timer = Mathf.Max(0.05f, interval);
        }
    }

    float MonsterAttenuation()
    {
        if (player == null || monster == null ||
            !player.gameObject.activeInHierarchy ||
            !monster.gameObject.activeInHierarchy)
        {
            return 0f;
        }

        float distance = Vector2.Distance(
            player.position, monster.position);

        float near = Mathf.Max(0f, fullVolumeDistance);
        float far = Mathf.Max(near + 0.1f, hearingDistance);

        return 1f - Mathf.InverseLerp(near, far, distance);
    }

    void UpdateVolumes()
    {
        if (playerSource == null)
            return;

        float attenuation = MonsterAttenuation();

        playerSource.volume = Mathf.Clamp01(playerFootstepVolume);
        monsterSource.volume =
            attenuation * Mathf.Clamp01(monsterMaxVolume);

        monsterScreamSource.volume =
            attenuation * Mathf.Clamp01(monsterScreamVolume);

        playerScreamSource.volume =
            Mathf.Clamp01(playerScreamVolume);

        musicSource.volume = Mathf.Clamp01(musicVolume);
    }

    void CheckScreams()
    {
        if (Time.timeScale == 0f)
            return;

        bool active = playerTrack.Active && monsterTrack.Active;

        // 使用与怪物 AI 相同的光照判断，包含墙壁遮挡。
        bool illuminated = active &&
            flashlight != null &&
            flashlight.IsIlluminating(monsterTrack.Position);

        // 只在“没被照 → 被照到”的瞬间触发。
        if (illuminated && !wasIlluminated &&
            monsterScream != null &&
            Time.time >= nextMonsterScream)
        {
            monsterScreamSource.Stop();
            monsterScreamSource.PlayOneShot(monsterScream);

            nextMonsterScream =
                Time.time + Mathf.Max(0f, monsterScreamCooldown);
        }

        wasIlluminated = illuminated;

        bool touching = active && CharactersTouching();

        // 持续贴着玩家不会每帧重复惨叫。
        if (touching && !wasTouching &&
            playerScream != null &&
            Time.time >= nextPlayerScream)
        {
            playerScreamSource.Stop();
            playerScreamSource.PlayOneShot(playerScream);

            nextPlayerScream =
                Time.time + Mathf.Max(0f, playerScreamCooldown);
        }

        wasTouching = touching;
    }

    bool CharactersTouching()
    {
        foreach (Collider2D monsterCollider in monsterTrack.colliders)
        {
            if (!UsableCollider(monsterCollider))
                continue;

            foreach (Collider2D playerCollider in playerTrack.colliders)
            {
                if (!UsableCollider(playerCollider))
                    continue;

                if (monsterCollider.IsTouching(playerCollider))
                    return true;
            }
        }

        return false;
    }

    bool UsableCollider(Collider2D collider)
    {
        // 只用实体碰撞体，避免检测范围触发器提前触发惨叫。
        return collider != null &&
            collider.enabled &&
            collider.gameObject.activeInHierarchy &&
            !collider.isTrigger;
    }

    public void PlayMusic(int number)
    {
        if (musicSource == null)
            return;

        AudioClip clip = number == 1 ? backgroundMusic1 :
                         number == 2 ? backgroundMusic2 : null;

        if (clip == null)
        {
            musicSource.Stop();
            musicSource.clip = null;
            return;
        }

        if (musicSource.clip == clip && musicSource.isPlaying)
            return;

        musicSource.Stop();
        musicSource.clip = clip;
        musicSource.volume = Mathf.Clamp01(musicVolume);
        musicSource.Play();
    }

    [ContextMenu("播放背景音乐 1")]
    public void PlayMusic1()
    {
        PlayMusic(1);
    }

    [ContextMenu("播放背景音乐 2")]
    public void PlayMusic2()
    {
        PlayMusic(2);
    }

    [ContextMenu("停止背景音乐")]
    public void StopMusic()
    {
        PlayMusic(0);
    }

    void OnDisable()
    {
        if (playerSource != null) playerSource.Stop();
        if (monsterSource != null) monsterSource.Stop();
        if (musicSource != null) musicSource.Stop();
        if (monsterScreamSource != null) monsterScreamSource.Stop();
        if (playerScreamSource != null) playerScreamSource.Stop();
    }
}