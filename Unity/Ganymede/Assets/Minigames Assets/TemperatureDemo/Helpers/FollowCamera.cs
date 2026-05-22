using UnityEngine;

// Makes this GameObject arc backward then forward while sliding horizontally to match the camera's movement
// in the x axis direction

public class CameraStepFollower : MonoBehaviour
{
    [Header("References")]
    [UnityEngine.SerializeField] private UnityEngine.Transform cameraTransform;

    [Header("Arc Settings")]
    [UnityEngine.Tooltip("How far back the object steps at the midpoint of the slide.")]
    [UnityEngine.SerializeField] private float stepBackDepth = 1.5f;

    [UnityEngine.Tooltip("How fast the arc completes its forward half once the camera stops.")]
    [UnityEngine.SerializeField] private float arcCompletionSpeed = 2f;

    [UnityEngine.Tooltip("How fast the arc builds while the camera is moving (0.1-0.5 recommended).")]
    [UnityEngine.SerializeField] private float arcBuildSpeed = 0.3f;

    [Header("Movement Detection")]
    [UnityEngine.Tooltip("How much the camera x must move per frame to be considered traveling.")]
    [UnityEngine.SerializeField] private float movementThreshold = 0.001f;

    [UnityEngine.Tooltip("Seconds the camera must be still before the forward arc triggers.")]
    [UnityEngine.SerializeField] private float stillnessDuration = 0.08f;

    // Locked world Y and Z baseline
    private float _lockedY;
    private float _lockedZ;

    // Arc progress: 0 = neutral, 0.5 = fully back, 1 = returned forward
    private float _arcT = 0f;
    private bool _traveling = false;
    private float _stillTimer = 0f;

    private float _camXPrev;

    private void Awake()
    {
        if (cameraTransform == null)
            cameraTransform = UnityEngine.Camera.main?.transform;
    }

    private void Start()
    {
        _lockedY = transform.position.y;
        _lockedZ = transform.position.z;
        _camXPrev = cameraTransform != null ? cameraTransform.position.x : 0f;
    }

    private void LateUpdate()
    {
        if (cameraTransform == null) 
            return;

        float camX = cameraTransform.position.x;
        float delta = UnityEngine.Mathf.Abs(camX - _camXPrev);

        //detect travel start 
        if (!_traveling && delta > movementThreshold)
        {
            _traveling  = true;
            _stillTimer = 0f;
            // Only reset arcT if it has fully completed a previous arc
            if (_arcT >= 1f || _arcT <= 0f)
                _arcT = 0f;
        }

        // detect travel end 
        if (_traveling && delta <= movementThreshold)
        {
            _stillTimer += UnityEngine.Time.deltaTime;
            if (_stillTimer >= stillnessDuration)
                _traveling = false;
        }
        else if (_traveling)
        {
            _stillTimer = 0f;
        }

        // drive arc T 
        if (_traveling)
        {
            // While the camera moves: push t toward 0.5 (the "back" peak)
            _arcT = UnityEngine.Mathf.MoveTowards(_arcT, 0.5f, arcBuildSpeed * UnityEngine.Time.deltaTime);
        }
        else if (_arcT > 0f)
        {
            // Camera stopped hence complete the arc toward 1 (step forward / return)
            _arcT = UnityEngine.Mathf.MoveTowards(_arcT, 1f, arcCompletionSpeed * UnityEngine.Time.deltaTime);

            //reset so next travel starts clean
            if (_arcT >= 1f) _arcT = 0f;
        }

        // Arc Z: sin(t * PI) gives 0 -> peak -> 0 shape
        float arcZ = -stepBackDepth * UnityEngine.Mathf.Sin(_arcT * UnityEngine.Mathf.PI);

        // Follow camera X directly 
        float newX = transform.position.x + (camX - _camXPrev);
        transform.position = new UnityEngine.Vector3(newX, _lockedY, _lockedZ + arcZ);
        _camXPrev = camX;
    }
}