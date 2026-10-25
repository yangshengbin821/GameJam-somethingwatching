using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

[RequireComponent(typeof(AudioSource))]
public class JumpscareScreen : MonoBehaviour
{
    public GameObject image1;
    public GameObject image2;
    public RectTransform image3;
    public TMP_Text shakingText;

    [Header("Audio")]
    public AudioClip audio1;
    [Range(0f, 1f)] public float audioVolume = 1f;

    [Header("Timing")]
    public float firstImageDuration = 1.5f;
    public float flashInterval = 0.07f;
    public int flashCount = 6;
    public float finalImageDuration = 2f;

    [Header("Main Menu")]
    public string mainMenuSceneName = "MainMenu";

    [Header("Text Shake")]
    public float letterShakeAmount = 3f;

    [Header("Final Image Shake")]
    public float imageShakeAmount = 25f;
    public float rotationShakeAmount = 4f;

    private Vector2 originalPosition;
    private Quaternion originalRotation;
    private AudioSource audioSource;

    private bool shakeText;
    private bool shakeImage;

    void Awake()
    {
        originalPosition = image3.anchoredPosition;
        originalRotation = image3.localRotation;

        image1.SetActive(true);
        image2.SetActive(false);
        image3.gameObject.SetActive(false);

        shakingText.gameObject.SetActive(true);
        shakingText.transform.SetAsLastSibling();

        audioSource = GetComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = audioVolume;
        audioSource.clip = audio1;

        if (audio1 != null)
            audioSource.Play();
    }

    IEnumerator Start()
    {
        shakeText = true;

        yield return new WaitForSecondsRealtime(
            Mathf.Max(0f, firstImageDuration)
        );

        shakeText = false;
        shakingText.gameObject.SetActive(false);
        image1.SetActive(false);

        for (int i = 0; i < flashCount; i++)
        {
            image2.SetActive(true);
            image3.gameObject.SetActive(false);

            yield return new WaitForSecondsRealtime(
                Mathf.Max(0.01f, flashInterval)
            );

            image2.SetActive(false);
            image3.gameObject.SetActive(true);

            yield return new WaitForSecondsRealtime(
                Mathf.Max(0.01f, flashInterval)
            );
        }

        image2.SetActive(false);
        image3.gameObject.SetActive(true);
        shakeImage = true;

        yield return new WaitForSecondsRealtime(
            Mathf.Max(0f, finalImageDuration)
        );

        shakeImage = false;
        audioSource.Stop();

        Time.timeScale = 1f;
        SceneManager.LoadScene(mainMenuSceneName);
    }

    void LateUpdate()
    {
        if (shakeText)
            ShakeLetters();

        if (shakeImage)
        {
            image3.anchoredPosition = originalPosition
                + Random.insideUnitCircle * imageShakeAmount;

            image3.localRotation = originalRotation
                * Quaternion.Euler(
                    0f,
                    0f,
                    Random.Range(
                        -rotationShakeAmount,
                        rotationShakeAmount
                    )
                );
        }
    }

    void ShakeLetters()
    {
        shakingText.ForceMeshUpdate();

        TMP_TextInfo textInfo = shakingText.textInfo;

        for (int i = 0; i < textInfo.characterCount; i++)
        {
            TMP_CharacterInfo character = textInfo.characterInfo[i];

            if (!character.isVisible)
                continue;

            Vector3[] vertices =
                textInfo.meshInfo[character.materialReferenceIndex].vertices;

            Vector3 offset =
                (Vector3)(Random.insideUnitCircle * letterShakeAmount);

            for (int j = 0; j < 4; j++)
            {
                vertices[character.vertexIndex + j] += offset;
            }
        }

        shakingText.UpdateVertexData(
            TMP_VertexDataUpdateFlags.Vertices
        );
    }
}