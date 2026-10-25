using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuScript : MonoBehaviour
{
    public string gameSceneName = "Game";

    public GameObject lightBackground;
    public float minOnTime = 0.5f;
    public float maxOnTime = 2.5f;
    public float minOffTime = 0.03f;
    public float maxOffTime = 0.2f;

    public GameObject loadingScreen;
    public GameObject secretImage;

    private bool isStarting;

    void Start()
    {
        loadingScreen.SetActive(false);
        secretImage.SetActive(false);
        StartCoroutine(Flicker());
    }

    public void StartGame()
    {
        if (isStarting)
            return;

        isStarting = true;
        StartCoroutine(ShowLoadingScreen());
    }

    IEnumerator ShowLoadingScreen()
    {
        loadingScreen.SetActive(true);
        loadingScreen.transform.SetAsLastSibling();

        secretImage.SetActive(Random.Range(0, 2) == 0);
        secretImage.transform.SetAsLastSibling();

        yield return new WaitForSecondsRealtime(Random.Range(2f, 3f));

        Time.timeScale = 1f;
        SceneManager.LoadScene(gameSceneName);
    }

    IEnumerator Flicker()
    {
        while (!isStarting && lightBackground != null)
        {
            lightBackground.SetActive(true);

            yield return new WaitForSecondsRealtime(
                Random.Range(minOnTime, maxOnTime)
            );

            if (isStarting)
                yield break;

            lightBackground.SetActive(false);

            yield return new WaitForSecondsRealtime(
                Random.Range(minOffTime, maxOffTime)
            );
        }
    }
}