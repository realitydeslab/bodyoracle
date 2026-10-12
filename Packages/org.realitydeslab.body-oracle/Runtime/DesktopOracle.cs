using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>Desktop-only presentation: full-window camera, the recognition frame, pose characters
/// and on-demand audio.</summary>
public sealed class DesktopOracle : MonoBehaviour
{
    [SerializeField] Yolo detector;
    [SerializeField] Camera displayCamera;
    [SerializeField] Sprite[] classSprites;
    [SerializeField] Sprite[] soundSprites;
    [SerializeField] AudioClip[] soundClips;
    [SerializeField] Renderer previewRenderer;
    [SerializeField] Text statusText;
    [SerializeField] Text selectionText;
    [SerializeField] Dropdown cameraDropdown;
    [SerializeField] Button soundButton;
    [SerializeField] Toggle previewToggle;
    [SerializeField] Image soundImage;
    [Tooltip("Grid for the guide frame, as on iOS; also outlines the recognised area when debugging.")]
    [SerializeField] Sprite recognitionFrame;
    [Tooltip("Outline the square the model actually sees. Toggle with D.")]
    [SerializeField] bool showRecognitionArea;
    GpuSpriteRenderer sprites;
    Material previewMaterial;
    // Two frames: the guide invites people to the middle of the view and is visual only; the
    // recognition area is the square the model sees, drawn only for debugging.
    Transform guide, recognitionArea;
    // As on iOS: the prompt sits under the guide frame and the pronunciation above it.
    Text guideText;
    const string PlacePrompt = "Place people in the middle of the grid.";
    const string PronouncePrompt = "Hold for 1 second or press Space to pronounce";
    // Holding a pose for one second pronounces it without pressing the button.
    readonly PoseHold poseHold = new PoseHold(1, 3);
    AudioSource audioSource;
    bool selecting, playing;
    float messageUntil;
    bool camerasInitialized;
    int cameraCount;

    void Start()
    {
        DetectionData.isDetecting = true;
        Application.targetFrameRate = 60;
        try { sprites = new GpuSpriteRenderer(classSprites); }
        catch (System.Exception e) { Debug.LogException(e, this); enabled = false; return; }
        previewMaterial = previewRenderer.material;
        if (recognitionFrame != null) guide = CreateFrame("Guide frame", Color.white);
        recognitionArea = CreateOutline("Recognition area (debug)", new Color(1, 0.2f, 0.15f));
        guideText = AddGuideText(PlacePrompt);
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        soundImage.gameObject.SetActive(false);
        soundButton.onClick.AddListener(SelectCharacter);
        previewToggle.onValueChanged.AddListener(SetPreviewVisible);
        RefreshCameras();
        cameraDropdown.onValueChanged.AddListener(ChangeCamera);
    }

    void RefreshCameras()
    {
        cameraDropdown.ClearOptions();
        var options = new List<string>();
        foreach (var device in WebCamTexture.devices) options.Add(device.name);
        cameraCount = options.Count;
        if (options.Count == 0) options.Add("No camera detected");
        cameraDropdown.AddOptions(options);
        int active = options.IndexOf(detector.ActiveWebcam);
        cameraDropdown.SetValueWithoutNotify(Mathf.Max(0, active));
    }

    void Update()
    {
        ShowCamera();
        if (Time.unscaledTime >= messageUntil) statusText.text = detector.CameraStatus;
        // The interface shows only the title and the play button; the status line appears for
        // camera problems and short prompts, never during normal recognition.
        statusText.enabled = !detector.HasFrame || Time.unscaledTime < messageUntil;
        soundButton.interactable = detector.HasFrame && !selecting && !playing;
        if (!camerasInitialized && detector.HasFrame) { RefreshCameras(); camerasInitialized = true; }
        cameraDropdown.interactable = cameraCount > 1 && !selecting;
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) SelectCharacter();
        if (Keyboard.current != null && Keyboard.current.dKey.wasPressedThisFrame) showRecognitionArea = !showRecognitionArea;
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) MacMenu.ExitFullScreen();
#endif
        if (playing && !audioSource.isPlaying) ResumeRecognition();
        if (!playing && !selecting) poseHold.Update(detector, Pronounce);
        if (guideText != null)
        {
            // While a pronunciation plays, only the pronunciation above the grid is shown.
            guideText.enabled = !playing;
            guideText.text = poseHold.HasCharacter ? PronouncePrompt : PlacePrompt;
        }
    }

    void LateUpdate()
    {
        if (detector.HasFrame && detector.isActiveAndEnabled)
            sprites?.Draw(detector.GpuResults, transform, 9, playing, gameObject.layer);
    }

    // The whole camera frame fills the window. The framed square in the middle is exactly what the
    // model sees (Yolo.CropScale of the frame's height, about its centre), with margins above and
    // below like the iOS view, so characters stay aligned with the people under them.
    void ShowCamera()
    {
        float square = detector.RecognisedPlaneHeight;
        Texture image = detector.CameraFrame;
        bool flipped = image != null && detector.CameraFrameFlipped;
        float aspect = 1, frameHeight = square;
        if (image != null)
        {
            aspect = (float)image.width / image.height;
            frameHeight = detector.ProjectionPlaneHeight;
        }
        else image = detector.CameraPreview; // video replay, or before the first camera frame
        previewRenderer.transform.localScale = new Vector3(frameHeight * aspect, frameHeight, 1);
        displayCamera.orthographicSize = .5f * Mathf.Min(frameHeight, frameHeight * aspect / displayCamera.aspect);
        // Nothing to show until the camera delivers; an empty quad would read as a white screen.
        previewRenderer.enabled = image != null;
        if (image != null) previewMaterial.SetTexture("_BaseMap", image);
        previewMaterial.SetTextureScale("_BaseMap", new Vector2(1, flipped ? -1 : 1));
        previewMaterial.SetTextureOffset("_BaseMap", new Vector2(0, flipped ? 1 : 0));
        if (guide != null) PlaceFrames(square);
    }

    // The prompt under the guide frame, in the interface font; placed every frame by PlaceFrames.
    Text AddGuideText(string text)
    {
        var label = soundButton.GetComponentInChildren<Text>(true); // the button is hidden with its bar
        var prompt = new GameObject("Guide prompt", typeof(RectTransform)).AddComponent<Text>();
        var rect = prompt.rectTransform;
        rect.SetParent(soundImage.transform.parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0);
        rect.pivot = new Vector2(.5f, 1);
        rect.sizeDelta = new Vector2(600, 30);
        prompt.font = label.font;
        prompt.fontSize = 20;
        prompt.alignment = TextAnchor.UpperCenter;
        prompt.color = Color.white;
        prompt.raycastTarget = false;
        prompt.text = text;
        return prompt;
    }

    // A solid outline, unlike the dashed guide, so the recognised square is unmistakable.
    Transform CreateOutline(string name, Color color)
    {
        var outline = new GameObject(name) { layer = previewRenderer.gameObject.layer };
        var line = outline.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = 4;
        line.SetPositions(new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(.5f, .5f), new Vector3(-.5f, .5f) });
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = line.endColor = color;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        outline.transform.position = previewRenderer.transform.position - new Vector3(0, 0, 0.2f);
        return outline.transform;
    }

    Transform CreateFrame(string name, Color color)
    {
        var grid = new GameObject(name) { layer = previewRenderer.gameObject.layer };
        var sprite = grid.AddComponent<SpriteRenderer>();
        sprite.sprite = recognitionFrame;
        sprite.color = color;
        // Just in front of the camera image, behind the characters.
        grid.transform.position = previewRenderer.transform.position - new Vector3(0, 0, 0.1f);
        return grid.transform;
    }

    // The guide is centred in the window with a 15% margin above and below; the pronunciation sits in
    // the upper margin and the prompt in the lower one. The debug outline marks the recognised square.
    void PlaceFrames(float square)
    {
        float spriteHeight = recognitionFrame.bounds.size.y;
        recognitionArea.gameObject.SetActive(showRecognitionArea);
        recognitionArea.localScale = new Vector3(square, square, 1);
        recognitionArea.GetComponent<LineRenderer>().widthMultiplier = square * .005f; // width is in world units
        const float margin = .15f;
        float top = displayCamera.ScreenToWorldPoint(new Vector3(0, Screen.height, 0)).y;
        float bottom = displayCamera.ScreenToWorldPoint(new Vector3(0, 0, 0)).y;
        var position = guide.position;
        position.y = (top + bottom) * .5f;
        guide.position = position;
        float size = (top - bottom) * (1 - 2 * margin);
        guide.localScale = Vector3.one * (size / spriteHeight);
        // Overlay canvas: a RectTransform's position is in screen pixels.
        float gap = 12 * soundImage.canvas.scaleFactor;
        float screenTop = displayCamera.WorldToScreenPoint(position + Vector3.up * size * .5f).y;
        float screenBottom = displayCamera.WorldToScreenPoint(position - Vector3.up * size * .5f).y;
        var soundRect = soundImage.rectTransform;
        soundRect.pivot = new Vector2(.5f, 0);
        soundRect.position = new Vector3(Screen.width * .5f, screenTop + gap, 0);
        if (guideText != null) guideText.rectTransform.position = new Vector3(Screen.width * .5f, screenBottom - gap, 0);
    }

    void SetPreviewVisible(bool visible) => previewRenderer.enabled = visible;

    void ChangeCamera(int index)
    {
        if (selecting) return;
        ResumeRecognition();
        detector.SelectWebcam(index);
    }

    void SelectCharacter()
    {
        if (selecting || playing || !detector.HasFrame) return;
        selecting = detector.RequestCenterDetection((found, result) =>
        {
            selecting = false;
            if (this == null || !isActiveAndEnabled) return;
            if (!found)
            {
                statusText.text = "Move into the centre of the camera view, then try again.";
                messageUntil = Time.unscaledTime + 3;
                return;
            }
            poseHold.MarkSpoken(result.predictedClassId);
            Pronounce(result);
        });
    }

    void Pronounce(DetectionData.PoseDetection result)
    {
        if (this == null || !isActiveAndEnabled || playing) return;
        int cls = result.predictedClassId;
        selectionText.text = "Character " + (cls + 1);
        if (cls >= soundClips.Length || soundClips[cls] == null) return;
        DetectionData.isDetecting = false;
        audioSource.clip = soundClips[cls];
        audioSource.Play();
        playing = true;
        if (cls < soundSprites.Length && soundSprites[cls] != null)
        {
            soundImage.sprite = soundSprites[cls];
            soundImage.gameObject.SetActive(true);
            KaraokeWipe.Attach(soundImage).Play(audioSource);
        }
    }

    void ResumeRecognition()
    {
        if (audioSource != null) audioSource.Stop();
        playing = false;
        if (soundImage != null) soundImage.gameObject.SetActive(false);
        if (selectionText != null) selectionText.text = "Stand back so the camera can see your whole pose.";
        DetectionData.isDetecting = true;
    }

    void OnDisable() { selecting = false; ResumeRecognition(); }
    void OnDestroy()
    {
        if (soundButton != null) soundButton.onClick.RemoveListener(SelectCharacter);
        if (previewToggle != null) previewToggle.onValueChanged.RemoveListener(SetPreviewVisible);
        if (cameraDropdown != null) cameraDropdown.onValueChanged.RemoveListener(ChangeCamera);
        sprites?.Dispose();
        if (previewMaterial != null) Destroy(previewMaterial);
    }
}
