using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a camera (or any transform) through a list of waypoints automatically,
/// smoothly lerping position and rotation between them. No user input required.
///
/// Usage:
///   1. Add this component to the Main Camera in the scene.
///   2. Either:
///        a) Create a few empty GameObjects positioned/rotated as you like and
///           drag them into the "Waypoints" list, OR
///        b) Press the "Capture Current Transform As Waypoint" context menu
///           item to bake fixed pose waypoints from the current camera.
///   3. Press Play. The camera will glide between shots automatically.
/// </summary>
[DisallowMultipleComponent]
public class CinematicCameraSequence : MonoBehaviour
{
    [System.Serializable]
    public class Shot
    {
        [Tooltip("Optional transform to follow. If set, overrides Position/Rotation below.")]
        public Transform waypoint;

        [Tooltip("Used when Waypoint is not assigned.")]
        public Vector3 position;
        [Tooltip("Used when Waypoint is not assigned.")]
        public Vector3 eulerRotation;

        [Tooltip("How long (seconds) it takes to travel from the previous shot to this one.")]
        [Min(0.01f)] public float travelDuration = 4f;

        [Tooltip("How long (seconds) to hold on this shot once reached.")]
        [Min(0f)] public float holdDuration = 1.5f;

        [Tooltip("Optional field of view. <= 0 keeps the previous value.")]
        public float fieldOfView = -1f;
    }

    [Header("Sequence")]
    [SerializeField] private List<Shot> shots = new List<Shot>();

    [Tooltip("Restart from the first shot once the last one finishes.")]
    [SerializeField] private bool loop = true;

    [Tooltip("Use smoothstep easing between shots for a cinematic feel.")]
    [SerializeField] private bool easeInOut = true;

    [Tooltip("Begin playing automatically on Start.")]
    [SerializeField] private bool playOnStart = true;

    [Tooltip("Delay (seconds) before the very first shot begins.")]
    [SerializeField, Min(0f)] private float startDelay = 0f;

    [Header("References")]
    [Tooltip("Camera to drive. If empty, uses the Camera on this GameObject (or Camera.main).")]
    [SerializeField] private Camera targetCamera;

    [Header("Conflict Handling")]
    [Tooltip("Disable FirstPersonCamera on this GameObject while the cinematic plays.")]
    [SerializeField] private bool disableFirstPersonCameraOnPlay = true;

    [Tooltip("Additional behaviours to disable while this sequence is playing.")]
    [SerializeField] private List<Behaviour> additionalBehavioursToDisable = new List<Behaviour>();

    private Transform _t;
    private float _timer;
    private int _currentIndex;
    private bool _playing;
    private readonly List<Behaviour> _disabledDuringPlayback = new List<Behaviour>();

    // Snapshot of the pose at the moment we started travelling toward _currentIndex.
    private Vector3 _fromPos;
    private Quaternion _fromRot;
    private float _fromFov;

    private enum Phase { Travel, Hold }
    private Phase _phase;

    private void Awake()
    {
        _t = transform;
        if (targetCamera == null)
        {
            targetCamera = GetComponent<Camera>();
            if (targetCamera == null) targetCamera = Camera.main;
        }
    }

    private void Start()
    {
        if (playOnStart) Play();
    }

    public void Play()
    {
        if (shots == null || shots.Count == 0)
        {
            Debug.LogWarning($"{nameof(CinematicCameraSequence)}: no shots configured.", this);
            return;
        }

        _currentIndex = 0;
        _phase = Phase.Travel;
        _timer = -startDelay;
        SnapshotCurrentPose();
        DisableConflictingControllers();
        _playing = true;
    }

    public void Stop()
    {
        _playing = false;
        RestoreConflictingControllers();
    }

    private void OnDisable()
    {
        RestoreConflictingControllers();
    }

    private void LateUpdate()
    {
        if (!_playing || shots.Count == 0) return;

        _timer += Time.deltaTime;
        if (_timer < 0f) return; // honoring start delay

        Shot shot = shots[_currentIndex];
        GetTargetPose(shot, out Vector3 toPos, out Quaternion toRot, out float toFov);

        if (_phase == Phase.Travel)
        {
            float t = Mathf.Clamp01(_timer / Mathf.Max(0.0001f, shot.travelDuration));
            float k = easeInOut ? t * t * (3f - 2f * t) : t;

            _t.position = Vector3.LerpUnclamped(_fromPos, toPos, k);
            _t.rotation = Quaternion.SlerpUnclamped(_fromRot, toRot, k);
            if (targetCamera != null && toFov > 0f)
                targetCamera.fieldOfView = Mathf.LerpUnclamped(_fromFov, toFov, k);

            if (t >= 1f)
            {
                _phase = Phase.Hold;
                _timer = 0f;
            }
        }
        else // Hold
        {
            // Keep tracking the live waypoint pose during the hold.
            _t.position = toPos;
            _t.rotation = toRot;
            if (targetCamera != null && toFov > 0f) targetCamera.fieldOfView = toFov;

            if (_timer >= shot.holdDuration)
            {
                AdvanceToNextShot();
            }
        }
    }

    private void AdvanceToNextShot()
    {
        SnapshotCurrentPose();

        if (_currentIndex + 1 >= shots.Count)
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

    private void SnapshotCurrentPose()
    {
        _fromPos = _t.position;
        _fromRot = _t.rotation;
        _fromFov = targetCamera != null ? targetCamera.fieldOfView : 60f;
    }

    private void GetTargetPose(Shot shot, out Vector3 pos, out Quaternion rot, out float fov)
    {
        if (shot.waypoint != null)
        {
            pos = shot.waypoint.position;
            rot = shot.waypoint.rotation;
        }
        else
        {
            pos = shot.position;
            rot = Quaternion.Euler(shot.eulerRotation);
        }
        fov = shot.fieldOfView;
    }

    private void DisableConflictingControllers()
    {
        _disabledDuringPlayback.Clear();

        if (disableFirstPersonCameraOnPlay)
        {
            var firstPerson = GetComponent("FirstPersonCamera") as Behaviour;
            TryDisableBehaviour(firstPerson);
        }

        for (int i = 0; i < additionalBehavioursToDisable.Count; i++)
        {
            TryDisableBehaviour(additionalBehavioursToDisable[i]);
        }
    }

    private void TryDisableBehaviour(Behaviour behaviour)
    {
        if (behaviour == null || behaviour == this || !behaviour.enabled) return;
        behaviour.enabled = false;
        _disabledDuringPlayback.Add(behaviour);
    }

    private void RestoreConflictingControllers()
    {
        for (int i = 0; i < _disabledDuringPlayback.Count; i++)
        {
            if (_disabledDuringPlayback[i] != null)
                _disabledDuringPlayback[i].enabled = true;
        }
        _disabledDuringPlayback.Clear();
    }

    [ContextMenu("Capture Current Transform As Waypoint")]
    private void CaptureCurrentTransformAsWaypoint()
    {
        var shot = new Shot
        {
            position = transform.position,
            eulerRotation = transform.eulerAngles,
            travelDuration = 4f,
            holdDuration = 1.5f,
            fieldOfView = targetCamera != null ? targetCamera.fieldOfView : -1f,
        };
        shots.Add(shot);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        Debug.Log($"Captured shot #{shots.Count} at {shot.position}.", this);
    }

    private void OnDrawGizmos()
    {
        if (shots == null || shots.Count == 0) return;

        Gizmos.color = Color.cyan;
        Vector3? prev = null;
        for (int i = 0; i < shots.Count; i++)
        {
            Vector3 p; Quaternion r;
            if (shots[i].waypoint != null) { p = shots[i].waypoint.position; r = shots[i].waypoint.rotation; }
            else { p = shots[i].position; r = Quaternion.Euler(shots[i].eulerRotation); }

            Gizmos.DrawWireSphere(p, 0.4f);
            Gizmos.DrawRay(p, r * Vector3.forward * 1.5f);
            if (prev.HasValue) Gizmos.DrawLine(prev.Value, p);
            prev = p;
        }
    }
}
