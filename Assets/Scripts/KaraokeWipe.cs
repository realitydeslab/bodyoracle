using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

/// <summary>KTV-style reading progress over a pronunciation image: a yellow copy of the image is
/// revealed left to right each time the pronunciation is spoken, and the text returns to white while
/// the "repeat" cue plays. Times come from Resources/PronunciationTiming.json
/// (Tools/pronunciation_timing.py): "clipN" lists pronunciations, "clipN/cue" the cue.</summary>
[RequireComponent(typeof(Image))]
public sealed class KaraokeWipe : MonoBehaviour
{
    static readonly Color Highlight = new Color(1f, 0.84f, 0.1f);
    static Dictionary<string, Vector2[]> timing;

    Image source, overlay;
    AudioSource audioSource;
    Vector2[] spoken, cues;

    /// <summary>Adds the wipe to an image once; later calls return the same component.</summary>
    public static KaraokeWipe Attach(Image image)
    {
        var wipe = image.GetComponent<KaraokeWipe>();
        return wipe != null ? wipe : image.gameObject.AddComponent<KaraokeWipe>();
    }

    void Awake()
    {
        source = GetComponent<Image>();
        var child = new GameObject("Reading progress", typeof(RectTransform));
        var rect = (RectTransform)child.transform;
        rect.SetParent(transform, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        overlay = child.AddComponent<Image>();
        overlay.raycastTarget = false;
        overlay.color = Highlight;
        overlay.type = Image.Type.Filled;
        overlay.fillMethod = Image.FillMethod.Horizontal;
        overlay.fillOrigin = (int)Image.OriginHorizontal.Left;
        overlay.fillAmount = 0;
    }

    /// <summary>Starts the wipe for the clip <paramref name="player"/> is playing now.</summary>
    public void Play(AudioSource player)
    {
        audioSource = player;
        overlay.sprite = source.sprite;
        overlay.preserveAspect = source.preserveAspect;
        overlay.material = source.material;
        spoken = Spans(player.clip, "");
        cues = Spans(player.clip, "/cue");
        overlay.fillAmount = 0;
    }

    void Update()
    {
        if (audioSource == null || spoken == null) return;
        overlay.fillAmount = audioSource.isPlaying ? Progress(audioSource.time) : 1;
    }

    // Sweeps while the pronunciation is spoken and holds full afterwards; the cue, and the silence
    // after it, show the text white again until the next repetition sweeps it.
    float Progress(float time)
    {
        float progress = 0, latest = float.MinValue;
        foreach (var span in spoken)
            if (time >= span.x && span.x > latest) { latest = span.x; progress = Mathf.InverseLerp(span.x, span.y, time); }
        foreach (var span in cues)
            if (time >= span.x && span.x > latest) { latest = span.x; progress = 0; }
        return progress;
    }

    static Vector2[] Spans(AudioClip clip, string suffix)
    {
        if (timing == null)
        {
            timing = new Dictionary<string, Vector2[]>();
            var asset = Resources.Load<TextAsset>("PronunciationTiming");
            if (asset != null)
                // One key per line, as the tool writes it: "clip1": [[0.27, 0.62], ...]
                foreach (Match entry in Regex.Matches(asset.text, @"^\s*""([^""]+)"":\s*(\[.*\])\s*,?\s*$", RegexOptions.Multiline))
                {
                    var ranges = new List<Vector2>();
                    foreach (Match pair in Regex.Matches(entry.Groups[2].Value, @"\[\s*([\d.]+)\s*,\s*([\d.]+)\s*\]"))
                        ranges.Add(new Vector2(float.Parse(pair.Groups[1].Value, CultureInfo.InvariantCulture),
                                               float.Parse(pair.Groups[2].Value, CultureInfo.InvariantCulture)));
                    timing[entry.Groups[1].Value] = ranges.ToArray();
                }
        }
        if (clip != null && timing.TryGetValue(clip.name + suffix, out var found)) return found;
        // Unknown clip: one sweep across the whole sound, no cue.
        return suffix.Length == 0 ? new[] { new Vector2(0, clip != null ? clip.length : 1) } : new Vector2[0];
    }
}
