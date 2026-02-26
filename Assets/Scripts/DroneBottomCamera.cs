using UnityEngine;

/// <summary>
/// Erstellt und verwaltet die nach unten gerichtete Kamera der Drohne
/// (Bodenkamera / Bottom Camera), wie sie beim Parrot AR.Drone 2.0
/// verbaut ist. Der echte Sensor ist eine QVGA-Kamera (320x240) mit
/// 60fps, die fuer optischen Fluss und Positionsstabilisierung genutzt wird.
///
/// Fuer ML-Agents:
///   Die Kamera erzeugt eine RenderTexture die von einem
///   Unity.MLAgents.Sensors.CameraSensorComponent als Visual Observation
///   genutzt werden kann. Einfach CameraSensorComponent auf das gleiche
///   GameObject legen und diese Kamera als Referenz setzen.
///
/// Montage:
///   Dieses Script auf ein leeres Child-GameObject unter der Drohne legen.
///   Position: ca. Mitte-Unterseite des Drohnen-Body.
///   Rotation: wird automatisch auf (90,0,0) gesetzt (nach unten schauend).
/// </summary>
public class DroneBottomCamera : MonoBehaviour
{
    [Header("Camera Settings")]
    [Tooltip("Aufloesung Breite (AR.Drone 2.0 Bottom = 320)")]
    public int resolutionWidth = 320;

    [Tooltip("Aufloesung Hoehe (AR.Drone 2.0 Bottom = 240)")]
    public int resolutionHeight = 240;

    [Tooltip("Field of View in Grad (AR.Drone 2.0 Bottom ~ 64 Grad)")]
    public float fieldOfView = 64f;

    [Tooltip("Nahebene der Kamera")]
    public float nearClip = 0.05f;

    [Tooltip("Fernebene der Kamera")]
    public float farClip = 50f;

    [Tooltip("Render-Tiefe (niedriger = wird frueher gerendert)")]
    public int renderDepth = -2;

    [Header("Rendering")]
    [Tooltip("Kamera deaktiviert rendern (nur in RenderTexture, nicht auf Screen)")]
    public bool offscreenOnly = true;

    [Tooltip("Anti-Aliasing fuer die RenderTexture (1=aus, 2/4/8)")]
    [Range(1, 8)]
    public int antiAliasing = 1;

    // ──────────────── Oeffentliche Referenzen ────────────────

    /// <summary>Die erzeugte Unity Camera-Komponente.</summary>
    public Camera BottomCam { get; private set; }

    /// <summary>Die RenderTexture in die gerendert wird.</summary>
    public RenderTexture RenderTex { get; private set; }

    // ──────────────── Lifecycle ────────────────

    void Awake()
    {
        SetupCamera();
    }

    void OnDestroy()
    {
        if (RenderTex != null)
        {
            RenderTex.Release();
            Destroy(RenderTex);
        }
    }

    // ──────────────── Setup ────────────────

    void SetupCamera()
    {
        // Rotation: nach unten schauen (lokaler X = 90 Grad)
        transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // Camera-Komponente erstellen oder finden
        BottomCam = GetComponent<Camera>();
        if (BottomCam == null)
            BottomCam = gameObject.AddComponent<Camera>();

        // Kamera-Parameter setzen
        BottomCam.fieldOfView = fieldOfView;
        BottomCam.nearClipPlane = nearClip;
        BottomCam.farClipPlane = farClip;
        BottomCam.depth = renderDepth;
        BottomCam.clearFlags = CameraClearFlags.SolidColor;
        BottomCam.backgroundColor = Color.black;

        // RenderTexture erstellen
        RenderTex = new RenderTexture(resolutionWidth, resolutionHeight, 16);
        RenderTex.antiAliasing = Mathf.ClosestPowerOfTwo(antiAliasing);
        RenderTex.filterMode = FilterMode.Bilinear;
        RenderTex.Create();

        BottomCam.targetTexture = RenderTex;

        // Wenn offscreen: Kamera nicht auf den Bildschirm rendern
        if (offscreenOnly)
        {
            BottomCam.enabled = true; // muss enabled bleiben fuer RenderTexture
            // targetTexture sorgt dafuer dass nichts auf Screen geht
        }
    }

    /// <summary>
    /// Erstellt die RenderTexture neu (z.B. bei Aenderung der Aufloesung zur Laufzeit).
    /// </summary>
    public void RecreateRenderTexture()
    {
        if (RenderTex != null)
        {
            RenderTex.Release();
            Destroy(RenderTex);
        }

        RenderTex = new RenderTexture(resolutionWidth, resolutionHeight, 16);
        RenderTex.antiAliasing = Mathf.ClosestPowerOfTwo(antiAliasing);
        RenderTex.filterMode = FilterMode.Bilinear;
        RenderTex.Create();

        if (BottomCam != null)
            BottomCam.targetTexture = RenderTex;
    }

    // ──────────────── Gizmos ────────────────

    void OnDrawGizmosSelected()
    {
        // Sichtfeld der Bodenkamera visualisieren
        Gizmos.color = new Color(0.5f, 0.5f, 1f, 0.3f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        float halfFov = fieldOfView * 0.5f * Mathf.Deg2Rad;
        float aspect = (float)resolutionWidth / resolutionHeight;
        float h = Mathf.Tan(halfFov) * farClip;
        float w = h * aspect;

        // Frustum-Linien
        Vector3 origin = Vector3.zero;
        Vector3 tl = new Vector3(-w, -h, farClip);
        Vector3 tr = new Vector3(w, -h, farClip);
        Vector3 bl = new Vector3(-w, h, farClip);
        Vector3 br = new Vector3(w, h, farClip);

        Gizmos.DrawLine(origin, tl);
        Gizmos.DrawLine(origin, tr);
        Gizmos.DrawLine(origin, bl);
        Gizmos.DrawLine(origin, br);
        Gizmos.DrawLine(tl, tr);
        Gizmos.DrawLine(bl, br);
        Gizmos.DrawLine(tl, bl);
        Gizmos.DrawLine(tr, br);

        Gizmos.matrix = Matrix4x4.identity;
    }
}
