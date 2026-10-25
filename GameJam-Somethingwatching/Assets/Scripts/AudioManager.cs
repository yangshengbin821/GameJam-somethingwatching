using UnityEngine;
using UnityEngine.SceneManagement;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("背景音乐")]
    public AudioClip mainMenuMusic;
    public AudioClip phase1Music;
    public AudioClip phase2Music;

    [Header("按钮音效")]
    public AudioClip buttonHover;
    public AudioClip startClick;

    [Header("游戏结果音效")]
    public AudioClip victory;
    public AudioClip failure;
    public AudioClip resurrection;
    public AudioClip endTaunt;

    [Header("恐怖音效")]
    public AudioClip ghostWailing;
    public AudioClip watchingYou;
    public AudioClip noWatchingType1;
    public AudioClip noWatchingType2;
    public AudioClip testSound;

    [Header("主角脚步：分别拖入左右脚")]
    public AudioClip playerStepLeft;
    public AudioClip playerStepRight;

    [Header("怪物脚步")]
    public AudioClip monsterWalking;

    [Header("左脚播放后，到右脚的间隔")]
    [Min(0.05f)] public float leftToRightInterval = 0.4f;

    [Header("右脚播放后，到左脚的间隔")]
    [Min(0.05f)] public float rightToLeftInterval = 0.4f;

    [Header("音量")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float musicVolume = 0.25f;
    [Range(0f, 1f)] public float soundVolume = 0.8f;
    [Range(0f, 1f)] public float playerFootstepVolume = 0.6f;
    [Range(0f, 1f)] public float monsterFootstepVolume = 0.8f;

    [Header("怪物脚步间隔：单位为秒")]
    [Min(0.05f)] public float normalStepInterval = 0.7f;
    [Min(0.05f)] public float retreatStepInterval = 0.35f;

    [Header("怪物声音距离：单位为 Unity 世界单位")]
    [Min(0f)] public float fullVolumeDistance = 2f;
    [Min(0.1f)] public float silentDistance = 15f;

    [Header("启动和切换场景")]
    public bool playMenuMusicOnStart = false;
    public bool stopSoundsOnSceneChange = true;

    private AudioSource musicSource;
    private AudioSource soundSource;
    private AudioSource playerSource;
    private AudioSource monsterSource;

    private bool playerIsMoving;
    private bool monsterIsMoving;
    private bool monsterIsRetreating;

    private float monsterDistance;
    private float stepTimer;
    private float playerStepTimer;
    private bool nextPlayerStepIsLeft = true;

    private void Awake()
    {
        // 只保留一个音频管理器。
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        musicSource = CreateSource(true);
        soundSource = CreateSource(false);
        playerSource = CreateSource(false);

        // 怪物使用单步音效，不能开启循环。
        monsterSource = CreateSource(false);
        monsterSource.pitch = 1f;

        ApplyVolumes();

        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    private void Start()
{
    PlayPhase1Music();
}

    private AudioSource CreateSource(bool loop)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f;

        return source;
    }

    private void Update()
    {
        ApplyVolumes();

        if (Time.timeScale <= 0f)
        {
            playerSource.Stop();
            monsterSource.Stop();
            stepTimer = 0f;
            playerStepTimer = 0f;
            return;
        }

        UpdatePlayerFootsteps();
        UpdateMonsterFootsteps();
    }

    private void ApplyVolumes()
    {
        float master = Mathf.Clamp01(masterVolume);

        musicSource.volume =
            master * Mathf.Clamp01(musicVolume);

        soundSource.volume =
            master * Mathf.Clamp01(soundVolume);

        playerSource.volume =
            master * Mathf.Clamp01(playerFootstepVolume);

        monsterSource.volume =
            master
            * Mathf.Clamp01(monsterFootstepVolume)
            * GetMonsterLoudness();
    }

    private float GetMonsterLoudness()
    {
        float near = Mathf.Max(0f, fullVolumeDistance);
        float far = Mathf.Max(near + 0.01f, silentDistance);

        return 1f - Mathf.InverseLerp(
            near, far, monsterDistance);
    }

    // ---------- 背景音乐 ----------

    public void PlayMusic(AudioClip clip)
    {
        if (clip == null) return;

        if (musicSource.clip == clip && musicSource.isPlaying)
            return;

        musicSource.clip = clip;
        musicSource.volume =
            Mathf.Clamp01(masterVolume)
            * Mathf.Clamp01(musicVolume);

        musicSource.Play();
    }

    public void StopMusic()
    {
        musicSource.Stop();
    }

    public void PlayMainMenuMusic()
        => PlayMusic(mainMenuMusic);

    public void PlayPhase1Music()
        => PlayMusic(phase1Music);

    public void PlayPhase2Music()
        => PlayMusic(phase2Music);

    // ---------- 普通音效 ----------

    // volumeScale 可以单独降低某一次音效的音量。
    public void PlaySound(AudioClip clip, float volumeScale = 1f)
    {
        if (clip == null) return;

        soundSource.volume =
            Mathf.Clamp01(masterVolume)
            * Mathf.Clamp01(soundVolume);

        soundSource.PlayOneShot(
            clip, Mathf.Clamp01(volumeScale));
    }

    public void PlayButtonHover()
        => PlaySound(buttonHover);

    public void PlayStartClick()
        => PlaySound(startClick);

    public void PlayVictory()
        => PlaySound(victory);

    public void PlayFailure()
        => PlaySound(failure);

    public void PlayResurrection()
        => PlaySound(resurrection);

    public void PlayEndTaunt()
        => PlaySound(endTaunt);

    public void PlayGhostWailing()
        => PlaySound(ghostWailing);

    public void PlayWatchingYou()
        => PlaySound(watchingYou);

    public void PlayNoWatchingType1()
        => PlaySound(noWatchingType1);

    public void PlayNoWatchingType2()
        => PlaySound(noWatchingType2);

    public void PlayTestSound()
        => PlaySound(testSound);

    // ---------- 主角脚步 ----------

    public void SetPlayerMoving(bool isMoving)
    {
        playerIsMoving = isMoving;
    }

    private void UpdatePlayerFootsteps()
    {
        playerStepTimer = Mathf.Max(
            0f, playerStepTimer - Time.deltaTime
        );

        if (!playerIsMoving || playerStepTimer > 0f)
            return;

        bool isLeftStep = nextPlayerStepIsLeft;

        AudioClip stepClip = isLeftStep
            ? playerStepLeft
            : playerStepRight;

        if (stepClip != null)
        {
            playerSource.Stop();
            playerSource.clip = stepClip;
            playerSource.Play();
        }

        playerStepTimer = Mathf.Max(
            0.05f,
            isLeftStep ? leftToRightInterval : rightToLeftInterval
        );

        nextPlayerStepIsLeft = !nextPlayerStepIsLeft;
    }

    // ---------- 怪物脚步 ----------

    // 怪物脚本每帧传入：
    // 是否移动、到玩家的距离、是否正在快速后退。
    public void SetMonsterFootsteps(
        bool isMoving,
        float distance,
        bool isRetreating)
    {
        bool justStarted = isMoving && !monsterIsMoving;
        bool justRetreated =
            isRetreating && !monsterIsRetreating;

        monsterDistance = Mathf.Max(0f, distance);
        monsterIsMoving = isMoving;
        monsterIsRetreating = isRetreating;

        if (!isMoving)
        {
            StopMonsterWalking();
            return;
        }

        // 开始走路时，立即触发第一步。
        if (justStarted)
        {
            stepTimer = 0f;
        }
        else if (justRetreated)
        {
            // 进入快速后退后，及时缩短下一步等待时间。
            stepTimer = Mathf.Min(
                stepTimer,
                Mathf.Max(0.05f, retreatStepInterval));
        }
    }

    private void UpdateMonsterFootsteps()
    {
        if (!monsterIsMoving)
            return;

        stepTimer -= Time.deltaTime;

        if (stepTimer > 0f)
            return;

        monsterSource.pitch = 1f;

        if (monsterWalking != null && monsterSource.volume > 0f)
            monsterSource.PlayOneShot(monsterWalking);

        float interval = monsterIsRetreating
            ? retreatStepInterval
            : normalStepInterval;

        stepTimer = Mathf.Max(0.05f, interval);
    }

    public void StopMonsterWalking()
    {
        monsterIsMoving = false;
        monsterIsRetreating = false;
        stepTimer = 0f;

        monsterSource.Stop();
    }

    // ---------- 停止和清理 ----------

    // 停止音效与脚步，背景音乐继续。
    public void StopAllSounds()
    {
        soundSource.Stop();
        SetPlayerMoving(false);
        StopMonsterWalking();
    }

    public void StopAllAudio()
    {
        StopMusic();
        StopAllSounds();
    }
    public void StartPlayerWalking()
    => SetPlayerMoving(true);

    public void StopPlayerWalking()
        => SetPlayerMoving(false);

    private void OnSceneChanged(Scene previous, Scene current)
    {
        if (stopSoundsOnSceneChange)
            StopAllSounds();
    }

    private void OnDestroy()
    {
        if (Instance != this) return;

        SceneManager.activeSceneChanged -= OnSceneChanged;
        Instance = null;
    }
}
