using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
public class ShepherdHUD : MonoBehaviour
{
    [Header("References")]
    public ShepherdGameManager gm;

    [Header("Top Bar")]
    public Text timerText;
    public Text scoreText;
    public Text roleText;
    public Text sessionCodeText;

    [Header("Drone Only")]
    public GameObject scarerCooldownRoot;
    public Image scarerCooldownBar;

    [Header("End Screen")]
    public GameObject endScreen;
    public Text resultTitleText;
    public Text resultBodyText;

    DronePlayer _dronePlayer;
    DroneScarer _scarer;

    void Start()
    {
        if (gm == null) gm = FindFirstObjectByType<ShepherdGameManager>();

        _dronePlayer = FindFirstObjectByType<DronePlayer>();
        if (_dronePlayer != null)
            _scarer = _dronePlayer.scarer;

        bool isDrone = _dronePlayer != null && _dronePlayer.IsLocal;
        if (scarerCooldownRoot != null) scarerCooldownRoot.SetActive(isDrone);
        if (roleText != null) roleText.text = isDrone ? "🚁 Drohne" : "🐺 Wolf";
        if (endScreen != null) endScreen.SetActive(false);

        if (sessionCodeText != null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            sessionCodeText.text = "Code: " + GetUrlParam("session");
#else
            sessionCodeText.text = "Code: EDITOR";
#endif
        }
    }

    void Update()
    {
        if (gm == null) return;
        UpdateTimer();
        UpdateScore();
        UpdateScarerBar();
    }

    void UpdateTimer()
    {
        if (timerText == null) return;
        float remaining = Mathf.Max(0, gm.roundDuration - gm.ElapsedTime);
        int min = Mathf.FloorToInt(remaining / 60f);
        int sec = Mathf.FloorToInt(remaining % 60f);
        timerText.text = $"{min:00}:{sec:00}";

        timerText.color = remaining < 30f
            ? (Time.time % 0.5f < 0.25f ? Color.red : Color.white)
            : Color.white;
    }

    void UpdateScore()
    {
        if (scoreText == null || gm == null) return;
        int caught = gm.SheepCaught;
        int safe = gm.ActiveSheep.Count;
        scoreText.text = $"Gefangen: {caught}  |  Sicher: {safe}";
    }

    void UpdateScarerBar()
    {
        if (scarerCooldownBar == null || _scarer == null) return;
        float fill = _scarer.IsActive ? (1f - _scarer.ActiveFraction) : (1f - _scarer.CooldownFraction);
        scarerCooldownBar.fillAmount = Mathf.Clamp01(fill);
        scarerCooldownBar.color = _scarer.IsReady ? new Color(0.3f, 0.8f, 1f) : Color.gray;
    }

    public void ShowEndScreen(int sheepSaved, int sheepCaught, float duration)
    {
        if (endScreen != null) endScreen.SetActive(true);
        if (resultTitleText != null) resultTitleText.text = "Runde beendet!";
        if (resultBodyText != null)
            resultBodyText.text = $"Schafe gerettet: {sheepSaved}\nSchafe gefangen: {sheepCaught}\nDauer: {Mathf.FloorToInt(duration / 60)}:{Mathf.FloorToInt(duration % 60):00}";
    }

    string GetUrlParam(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return GetUrlParameterHUD(key);
#else
        return "";
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern string GetUrlParameterHUD(string key);
#endif
}
