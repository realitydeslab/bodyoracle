using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>Desktop-only presentation: webcam preview, pose characters and on-demand audio.</summary>
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
    GpuSpriteRenderer sprites;
    Material previewMaterial;
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
        // Keep the square camera view and recognition coordinates aligned at every window aspect.
        displayCamera.orthographicSize = detector.ProjectionPlaneHeight * .5f / Mathf.Min(1, displayCamera.aspect);
        if (detector.CameraPreview != null) previewMaterial.SetTexture("_BaseMap", detector.CameraPreview);
        if (Time.unscaledTime >= messageUntil) statusText.text = detector.CameraStatus;
        soundButton.interactable = detector.HasFrame && !selecting && !playing;
        if (!camerasInitialized && detector.HasFrame) { RefreshCameras(); camerasInitialized = true; }
        cameraDropdown.interactable = cameraCount > 1 && !selecting;
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) SelectCharacter();
        if (playing && !audioSource.isPlaying) ResumeRecognition();
    }

    void LateUpdate()
    {
        if (detector.HasFrame && detector.isActiveAndEnabled)
            sprites?.Draw(detector.GpuResults, transform, 9, playing, gameObject.layer);
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
            }
        });
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
