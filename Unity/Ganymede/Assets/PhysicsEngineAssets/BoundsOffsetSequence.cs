using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a UseComputePlugin's transform through a list of position keyframes,
/// smoothly lerping the AABB bounds offset over time. Works like
/// CinematicCameraSequence but for simulation bounds instead of a camera.
///
/// Usage:
///   1. Attach to the same GameObject as UseComputePlugin (or any object).
///   2. Assign the UseComputePlugin reference.
///   3. Add keyframes with target X/Z offsets, travel & hold durations.
///   4. Press Play.
/// </summary>
[DisallowMultipleComponent]
public class BoundsOffsetSequence : MonoBehaviour
{
    [System.Serializable]
    public class Keyframe
    {
        [Tooltip("Target X offset from starting position.")]
        public float xOffset;

        [Tooltip("Target Z offset from starting position.")]
        public float zOffset;

        [Tooltip("Seconds to travel from the previous keyframe to this one.")]
        [Min(0.01f)] public float travelDuration = 3f;

        [Tooltip("Seconds to hold at this position once reached.")]
        [Min(0f)] public float holdDuration = 1f;

        [Tooltip("Optional: also change boundsMin. Leave at zero to keep current.")]
        public Vector3 boundsMinOverride;

        [Tooltip("Optional: also change boundsMax. Leave at zero to keep current.")]
        public Vector3 boundsMaxOverride;

        [Tooltip("Enable to apply boundsMin/Max overrides at this keyframe.")]
        public bool applyBoundsOverride;

        [Tooltip("Target simulation speed (Time.timeScale). 1 = normal, 0.1 = slow motion.")]
        [Range(0.01f, 1f)] public float timeScale = 1f;
    }

    [Header("References")]
    [Tooltip("The UseComputePlugin whose transform controls the AABB position.")]
    public UseComputePlugin computePlugin;

    [Tooltip("Camera to move along with the bounds offset. If empty, uses Camera.main.")]
    public Camera targetCamera;

    [Header("Sequence")]
    [SerializeField] private List<Keyframe> keyframes = new List<Keyframe>();

    [Tooltip("Restart from the first keyframe once the last one finishes.")]
    [SerializeField] private bool loop = true;

    [Tooltip("Use smoothstep easing between keyframes.")]
    [SerializeField] private bool easeInOut = true;

    [Tooltip("Begin playing automatically on Start.")]
    [SerializeField] private bool playOnStart = true;

    [Tooltip("Delay (seconds) before the sequence begins.")]
    [SerializeField, Min(0f)] private float startDelay = 0f;

    // Internal state
    private Vector3 _startPos;
    private Vector3 _cameraStartPos;
    private float _timer;
    private int _currentIndex;
    private bool _playing;

    private Vector3 _fromPos;
    private Vector3 _fromBoundsMin;
    private Vector3 _fromBoundsMax;
    private float _fromTimeScale;

    private enum Phase { Travel, Hold }
    private Phase _phase;

    private void Start()
    {
        if (computePlugin == null)
            computePlugin = GetComponent<UseComputePlugin>();

        if (computePlugin != null)
            _startPos = computePlugin.transform.position;

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera != null)
            _cameraStartPos = targetCamera.transform.position;

        if (playOnStart) Play();
    }

    public void Play()
    {
        if (keyframes == null || keyframes.Count == 0)
        {
            Debug.LogWarning($"{nameof(BoundsOffsetSequence)}: no keyframes configured.", this);
            return;
        }

        _currentIndex = 0;
        _phase = Phase.Travel;
        _timer = -startDelay;
        SnapshotCurrent();
        _playing = true;
    }

    public void Stop()
    {
        _playing = false;
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
    }

    private void OnDestroy()
    {
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
    }

    private void Update()
    {
        if (!_playing || keyframes.Count == 0 || computePlugin == null) return;

        _timer += Time.unscaledDeltaTime;
        if (_timer < 0f) return;

        Keyframe kf = keyframes[_currentIndex];
        Vector3 toPos = _startPos + new Vector3(kf.xOffset, 0f, kf.zOffset);

        if (_phase == Phase.Travel)
        {
            float t = Mathf.Clamp01(_timer / Mathf.Max(0.0001f, kf.travelDuration));
            float k = easeInOut ? t * t * (3f - 2f * t) : t;

            Vector3 boundsPos = Vector3.LerpUnclamped(_fromPos, toPos, k);
            computePlugin.transform.position = boundsPos;
            ApplyCameraOffset(boundsPos);

            float targetTimeScale = Mathf.LerpUnclamped(_fromTimeScale, kf.timeScale, k);
            Time.timeScale = targetTimeScale;
            Time.fixedDeltaTime = 0.02f * targetTimeScale;

            if (kf.applyBoundsOverride)
            {
                computePlugin.boundsMin = Vector3.LerpUnclamped(_fromBoundsMin, kf.boundsMinOverride, k);
                computePlugin.boundsMax = Vector3.LerpUnclamped(_fromBoundsMax, kf.boundsMaxOverride, k);
            }

            if (t >= 1f)
            {
                _phase = Phase.Hold;
                _timer = 0f;
            }
        }
        else
        {
            computePlugin.transform.position = toPos;
            ApplyCameraOffset(toPos);

            Time.timeScale = kf.timeScale;
            Time.fixedDeltaTime = 0.02f * kf.timeScale;

            if (kf.applyBoundsOverride)
            {
                computePlugin.boundsMin = kf.boundsMinOverride;
                computePlugin.boundsMax = kf.boundsMaxOverride;
            }

            if (_timer >= kf.holdDuration)
            {
                AdvanceToNextKeyframe();
            }
        }
    }

    private void AdvanceToNextKeyframe()
    {
        SnapshotCurrent();

        if (_currentIndex + 1 >= keyframes.Count)
        {
            if (loop) _currentIndex = 0;
            else { _playing = false; return; }
        }
        else
        {
            _currentIndex++;
        }

        _phase = Phase.Travel;
        _timer = 0f;
    }

    private void ApplyCameraOffset(Vector3 currentBoundsPos)
    {
        if (targetCamera == null) return;
        Vector3 delta = currentBoundsPos - _startPos;
        targetCamera.transform.position = _cameraStartPos + new Vector3(delta.x, 0f, delta.z);
    }

    private void SnapshotCurrent()
    {
        _fromPos = computePlugin.transform.position;
        _fromBoundsMin = computePlugin.boundsMin;
        _fromBoundsMax = computePlugin.boundsMax;
        _fromTimeScale = Time.timeScale;
    }

    [ContextMenu("Capture Current Offset As Keyframe")]
    private void CaptureCurrentOffsetAsKeyframe()
    {
        if (computePlugin == null)
        {
            Debug.LogWarning("No UseComputePlugin assigned.", this);
            return;
        }

        Vector3 offset = computePlugin.transform.position - _startPos;
        var kf = new Keyframe
        {
            xOffset = offset.x,
            zOffset = offset.z,
            travelDuration = 3f,
            holdDuration = 1f,
            boundsMinOverride = computePlugin.boundsMin,
            boundsMaxOverride = computePlugin.boundsMax,
            applyBoundsOverride = false,
            timeScale = Time.timeScale,
        };
        keyframes.Add(kf);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        Debug.Log($"Captured keyframe #{keyframes.Count}: X={kf.xOffset:F1}, Z={kf.zOffset:F1}", this);
    }

    private void OnDrawGizmos()
    {
        if (keyframes == null || keyframes.Count == 0) return;

        Vector3 origin = Application.isPlaying ? _startPos :
            (computePlugin != null ? computePlugin.transform.position : transform.position);

        Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.8f);
        Vector3? prev = null;
        for (int i = 0; i < keyframes.Count; i++)
        {
            Vector3 p = origin + new Vector3(keyframes[i].xOffset, 0f, keyframes[i].zOffset);
            Gizmos.DrawWireSphere(p, 0.3f);
            if (prev.HasValue) Gizmos.DrawLine(prev.Value, p);
            prev = p;
        }
    }
}
