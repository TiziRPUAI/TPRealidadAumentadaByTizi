using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
#endif

/// <summary>
/// Estados del flujo de detección y colocación en Realidad Aumentada.
/// </summary>
public enum ARPlacementState
{
    Scanning = 0,
    SurfaceDetected = 1,
    ObjectActive = 2
}

/// <summary>
/// Gestiona la detección de planos horizontales, el retículo procedural de apuntado,
/// el spawneo/reposicionamiento suave y la manipulación táctil y programática del objeto 3D en AR.
/// Compatible con Unity 6 (6000.4.1f1), AR Foundation 6.x e iOS (ARKit).
/// </summary>
[DisallowMultipleComponent]
public sealed class ARPlacementManager : MonoBehaviour
{
    public const float MinScaleFactor = 0.2f;
    public const float MaxScaleFactor = 3.0f;

    private static readonly int BaseColorShaderId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorShaderId = Shader.PropertyToID("_Color");

    [Header("Referencias AR")]
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private Camera arCamera;

    [Header("Prefabs y Materiales (Opcionales - Fallback Procedural Automático)")]
    [SerializeField] private GameObject spawnablePrefab;
    [SerializeField] private Material reticleMaterial;
    [SerializeField] private Material defaultObjectMaterial;

    [Header("Parámetros de Movimiento y Escala")]
    [SerializeField, Range(2f, 30f)] private float smoothRepositionSpeed = 12f;
    [SerializeField, Range(0.05f, 0.25f)] private float reticleOuterRadius = 0.095f;
    [SerializeField, Range(0.03f, 0.20f)] private float reticleInnerRadius = 0.074f;

    private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>(16);
    private readonly List<RaycastResult> _uiRaycastResults = new List<RaycastResult>(16);
    private readonly List<Material> _colorTargetMaterials = new List<Material>(8);
    private readonly List<Material> _instancedMaterials = new List<Material>(8);

    private PointerEventData _cachedPointerEventData;
    private EventSystem _cachedEventSystem;

    private GameObject _reticleObject;
    private Mesh _reticleMesh;
    private Material _runtimeReticleMaterial;

    private GameObject _spawnedObject;
    private Vector3 _baseObjectScale = Vector3.one;
    private Vector3 _targetPosition;
    private bool _isRepositioning;

    private float _currentScaleFactor = 1.0f;
    private Color _currentColor = new Color(0.16f, 0.50f, 0.95f, 1.0f);
    private bool _hasCustomColor;

    // Estado de gestos multitáctiles (pellizco de escala y giro a dos dedos)
    private bool _touchStartedOverUI;
    private bool _isMultiTouchActive;
    private float _initialPinchDistance;
    private float _initialPinchScale;
    private float _previousTwistAngle;

    /// <summary>
    /// Evento disparado cada vez que cambia el estado de detección o colocación.
    /// </summary>
    public event Action<ARPlacementState> OnStateChanged;

    /// <summary>
    /// Evento disparado cuando cambia el factor de escala (p. ej. por pellizco táctil o reinicio).
    /// </summary>
    public event Action<float> OnScaleChanged;

    public ARPlacementState CurrentState { get; private set; } = ARPlacementState.Scanning;
    public bool HasPlacedObject => _spawnedObject != null;
    public bool IsReticleVisible => _reticleObject != null && _reticleObject.activeSelf;
    public float CurrentScaleFactor => _currentScaleFactor;

    private void Awake()
    {
        ResolveARReferences();
        ConfigureHorizontalPlaneDetection();
        CreateProceduralReticle();
    }

    private void OnEnable()
    {
#if ENABLE_INPUT_SYSTEM
        if (!EnhancedTouchSupport.enabled)
        {
            EnhancedTouchSupport.Enable();
        }
#endif
        if (planeManager != null)
        {
            planeManager.trackablesChanged.AddListener(OnPlanesTrackablesChanged);
        }

        EvaluateAndBroadcastState(forceNotify: true);
    }

    private void OnDisable()
    {
        if (planeManager != null)
        {
            planeManager.trackablesChanged.RemoveListener(OnPlanesTrackablesChanged);
        }

#if ENABLE_INPUT_SYSTEM
        if (EnhancedTouchSupport.enabled)
        {
            EnhancedTouchSupport.Disable();
        }
#endif
    }

    private void OnDestroy()
    {
        CleanupInstancedObjectMaterials();

        if (_runtimeReticleMaterial != null)
        {
            Destroy(_runtimeReticleMaterial);
            _runtimeReticleMaterial = null;
        }

        if (_reticleMesh != null)
        {
            Destroy(_reticleMesh);
            _reticleMesh = null;
        }

        if (_reticleObject != null)
        {
            Destroy(_reticleObject);
            _reticleObject = null;
        }
    }

    private void Update()
    {
        if (arCamera == null || raycastManager == null)
        {
            ResolveARReferences();
        }

        UpdateCenterReticle();
        ProcessTouchInput();
        UpdateSmoothRepositioning();
        EvaluateAndBroadcastState(forceNotify: false);
    }

    /// <summary>
    /// Ajusta la escala local del objeto activo entre 0.2x y 3.0x respecto de su escala base.
    /// </summary>
    /// <param name="factor">Factor multiplicador de escala.</param>
    public void SetScale(float factor)
    {
        float clampedFactor = Mathf.Clamp(factor, MinScaleFactor, MaxScaleFactor);
        bool changed = !Mathf.Approximately(_currentScaleFactor, clampedFactor);
        _currentScaleFactor = clampedFactor;

        if (_spawnedObject != null)
        {
            _spawnedObject.transform.localScale = _baseObjectScale * _currentScaleFactor;
        }

        if (changed)
        {
            OnScaleChanged?.Invoke(_currentScaleFactor);
        }
    }

    /// <summary>
    /// Rota el objeto activo sobre su eje vertical (Y).
    /// </summary>
    /// <param name="degrees">Ángulo de rotación en grados.</param>
    public void RotateY(float degrees)
    {
        if (_spawnedObject == null || Mathf.Approximately(degrees, 0f))
        {
            return;
        }

        _spawnedObject.transform.Rotate(0f, degrees, 0f, Space.Self);
    }

    /// <summary>
    /// Altera el color del material principal del objeto 3D activo.
    /// </summary>
    /// <param name="newColor">Nuevo color a aplicar.</param>
    public void ChangeColor(Color newColor)
    {
        _currentColor = newColor;
        _hasCustomColor = true;

        if (_spawnedObject == null)
        {
            return;
        }

        ApplyColorToActiveRenderers(newColor);
    }

    /// <summary>
    /// Destruye el objeto activo, limpia materiales instanciados y reinicia el estado de colocación.
    /// </summary>
    public void ResetPlacement()
    {
        CleanupInstancedObjectMaterials();

        if (_spawnedObject != null)
        {
            Destroy(_spawnedObject);
            _spawnedObject = null;
        }

        _isRepositioning = false;
        _baseObjectScale = Vector3.one;
        _currentScaleFactor = 1.0f;

        OnScaleChanged?.Invoke(_currentScaleFactor);
        EvaluateAndBroadcastState(forceNotify: true);
    }

    private void ResolveARReferences()
    {
        if (xrOrigin == null)
        {
            xrOrigin = FindAnyObjectByType<XROrigin>();
        }

        if (raycastManager == null)
        {
            raycastManager = GetComponent<ARRaycastManager>();
            if (raycastManager == null)
            {
                raycastManager = FindAnyObjectByType<ARRaycastManager>();
            }
        }

        if (planeManager == null)
        {
            planeManager = GetComponent<ARPlaneManager>();
            if (planeManager == null)
            {
                planeManager = FindAnyObjectByType<ARPlaneManager>();
            }
        }

        if (arCamera == null)
        {
            if (xrOrigin != null && xrOrigin.Camera != null)
            {
                arCamera = xrOrigin.Camera;
            }
            else
            {
                arCamera = Camera.main;
            }
        }
    }

    private void ConfigureHorizontalPlaneDetection()
    {
        if (planeManager != null)
        {
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
        }
    }

    private void OnPlanesTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> eventArgs)
    {
        EvaluateAndBroadcastState(forceNotify: false);
    }

    private void UpdateCenterReticle()
    {
        if (_reticleObject == null || raycastManager == null)
        {
            return;
        }

        if (Screen.width <= 0 || Screen.height <= 0)
        {
            _reticleObject.SetActive(false);
            return;
        }

        Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        if (TryRaycastHorizontalPlane(screenCenter, out Pose hitPose))
        {
            if (!_reticleObject.activeSelf)
            {
                _reticleObject.SetActive(true);
            }

            _reticleObject.transform.SetPositionAndRotation(hitPose.position, hitPose.rotation);

            // Pulso visual sutil para indicar tracking activo sobre el plano horizontal
            float pulse = 1.0f + 0.05f * Mathf.Sin(Time.unscaledTime * 4.5f);
            _reticleObject.transform.localScale = new Vector3(pulse, 1.0f, pulse);
        }
        else if (_reticleObject.activeSelf)
        {
            _reticleObject.SetActive(false);
        }
    }

    private void ProcessTouchInput()
    {
#if ENABLE_INPUT_SYSTEM
        var activeTouches = Touch.activeTouches;
        int touchCount = activeTouches.Count;

        if (touchCount == 0)
        {
            _isMultiTouchActive = false;
            _touchStartedOverUI = false;

#if UNITY_EDITOR
            // Soporte en el Editor (XR Simulation / Game View) sin incluir APIs de escritorio en compilación iOS
            Pointer pointer = Pointer.current;
            if (pointer != null && pointer.press.wasPressedThisFrame)
            {
                Vector2 pointerPos = pointer.position.ReadValue();
                if (!IsScreenPointOverUI(pointerPos))
                {
                    HandlePlacementOrRepositionTap(pointerPos);
                }
            }
#endif
            return;
        }

        // Manipulación táctil a dos dedos (pellizco para escalar y rotación sobre eje Y)
        if (touchCount >= 2)
        {
            Touch touch0 = activeTouches[0];
            Touch touch1 = activeTouches[1];

            if (!_isMultiTouchActive)
            {
                _isMultiTouchActive = true;
                if (IsScreenPointOverUI(touch0.screenPosition) || IsScreenPointOverUI(touch1.screenPosition))
                {
                    _touchStartedOverUI = true;
                    return;
                }

                _touchStartedOverUI = false;
                _initialPinchDistance = Vector2.Distance(touch0.screenPosition, touch1.screenPosition);
                _initialPinchScale = _currentScaleFactor;
                _previousTwistAngle = GetAngleDegrees(touch0.screenPosition, touch1.screenPosition);
                return;
            }

            if (_touchStartedOverUI || _spawnedObject == null)
            {
                return;
            }

            float currentDistance = Vector2.Distance(touch0.screenPosition, touch1.screenPosition);
            if (_initialPinchDistance > 5f)
            {
                float scaleRatio = currentDistance / _initialPinchDistance;
                SetScale(_initialPinchScale * scaleRatio);
            }

            float currentAngle = GetAngleDegrees(touch0.screenPosition, touch1.screenPosition);
            float angleDelta = Mathf.DeltaAngle(_previousTwistAngle, currentAngle);
            _previousTwistAngle = currentAngle;

            if (Mathf.Abs(angleDelta) > 0.05f)
            {
                RotateY(-angleDelta);
            }

            return;
        }

        // Si venimos saliendo de un gesto multitáctil, esperar a que se levanten todos los dedos
        if (_isMultiTouchActive)
        {
            return;
        }

        Touch primaryTouch = activeTouches[0];
        if (primaryTouch.phase == TouchPhase.Began)
        {
            _touchStartedOverUI = IsScreenPointOverUI(primaryTouch.screenPosition);
            if (!_touchStartedOverUI)
            {
                HandlePlacementOrRepositionTap(primaryTouch.screenPosition);
            }
        }
        else if (primaryTouch.phase == TouchPhase.Moved && !_touchStartedOverUI && _spawnedObject != null)
        {
            // Permite arrastrar suavemente el objeto sobre el plano horizontal con un dedo
            if (TryRaycastHorizontalPlane(primaryTouch.screenPosition, out Pose dragPose))
            {
                _targetPosition = dragPose.position;
                _isRepositioning = true;
            }
        }
#else
        if (Input.touchCount == 0)
        {
            _isMultiTouchActive = false;
            _touchStartedOverUI = false;
            return;
        }

        if (Input.touchCount >= 2)
        {
            UnityEngine.Touch t0 = Input.GetTouch(0);
            UnityEngine.Touch t1 = Input.GetTouch(1);

            if (!_isMultiTouchActive)
            {
                _isMultiTouchActive = true;
                _touchStartedOverUI = IsScreenPointOverUI(t0.position) || IsScreenPointOverUI(t1.position);
                _initialPinchDistance = Vector2.Distance(t0.position, t1.position);
                _initialPinchScale = _currentScaleFactor;
                _previousTwistAngle = GetAngleDegrees(t0.position, t1.position);
                return;
            }

            if (!_touchStartedOverUI && _spawnedObject != null)
            {
                float currentDist = Vector2.Distance(t0.position, t1.position);
                if (_initialPinchDistance > 5f)
                {
                    SetScale(_initialPinchScale * (currentDist / _initialPinchDistance));
                }

                float currentAng = GetAngleDegrees(t0.position, t1.position);
                float deltaAng = Mathf.DeltaAngle(_previousTwistAngle, currentAng);
                _previousTwistAngle = currentAng;
                RotateY(-deltaAng);
            }
            return;
        }

        if (_isMultiTouchActive)
        {
            return;
        }

        UnityEngine.Touch touch = Input.GetTouch(0);
        if (touch.phase == UnityEngine.TouchPhase.Began)
        {
            _touchStartedOverUI = IsScreenPointOverUI(touch.position);
            if (!_touchStartedOverUI)
            {
                HandlePlacementOrRepositionTap(touch.position);
            }
        }
        else if (touch.phase == UnityEngine.TouchPhase.Moved && !_touchStartedOverUI && _spawnedObject != null)
        {
            if (TryRaycastHorizontalPlane(touch.position, out Pose dragPose))
            {
                _targetPosition = dragPose.position;
                _isRepositioning = true;
            }
        }
#endif
    }

    private static float GetAngleDegrees(Vector2 from, Vector2 to)
    {
        Vector2 diff = to - from;
        return Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;
    }

    private void HandlePlacementOrRepositionTap(Vector2 screenPosition)
    {
        if (!TryRaycastHorizontalPlane(screenPosition, out Pose hitPose))
        {
            return;
        }

        if (_spawnedObject == null)
        {
            SpawnObjectAtPose(hitPose);
        }
        else
        {
            _targetPosition = hitPose.position;
            _isRepositioning = true;
        }
    }

    private void SpawnObjectAtPose(Pose hitPose)
    {
        CleanupInstancedObjectMaterials();

        Quaternion spawnRotation = CalculateCameraFacingYaw(hitPose.position, hitPose.rotation);

        if (spawnablePrefab != null)
        {
            _spawnedObject = Instantiate(spawnablePrefab, hitPose.position, spawnRotation);
            _spawnedObject.name = "AR Interactive Object";
            CachePrefabObjectMaterials(_spawnedObject);
        }
        else
        {
            _spawnedObject = BuildProceduralInteractiveObject(hitPose.position, spawnRotation);
        }

        _baseObjectScale = _spawnedObject.transform.localScale;
        _spawnedObject.transform.localScale = _baseObjectScale * _currentScaleFactor;

        _targetPosition = hitPose.position;
        _isRepositioning = false;

        if (_hasCustomColor)
        {
            ApplyColorToActiveRenderers(_currentColor);
        }

        EvaluateAndBroadcastState(forceNotify: true);
    }

    private Quaternion CalculateCameraFacingYaw(Vector3 hitPosition, Quaternion fallbackRotation)
    {
        if (arCamera == null)
        {
            return fallbackRotation;
        }

        Vector3 toCamera = arCamera.transform.position - hitPosition;
        Vector3 flatForward = Vector3.ProjectOnPlane(toCamera, Vector3.up);
        if (flatForward.sqrMagnitude < 0.0001f)
        {
            return fallbackRotation;
        }

        return Quaternion.LookRotation(flatForward.normalized, Vector3.up);
    }

    private void UpdateSmoothRepositioning()
    {
        if (!_isRepositioning || _spawnedObject == null)
        {
            return;
        }

        Vector3 currentPos = _spawnedObject.transform.position;
        float interpolation = 1.0f - Mathf.Exp(-smoothRepositionSpeed * Time.deltaTime);
        Vector3 nextPos = Vector3.Lerp(currentPos, _targetPosition, interpolation);

        if ((nextPos - _targetPosition).sqrMagnitude <= 0.000001f)
        {
            _spawnedObject.transform.position = _targetPosition;
            _isRepositioning = false;
        }
        else
        {
            _spawnedObject.transform.position = nextPos;
        }
    }

    private bool TryRaycastHorizontalPlane(Vector2 screenPoint, out Pose hitPose)
    {
        hitPose = Pose.identity;

        if (raycastManager == null || !raycastManager.enabled)
        {
            return false;
        }

        _raycastHits.Clear();
        if (!raycastManager.Raycast(screenPoint, _raycastHits, TrackableType.PlaneWithinPolygon))
        {
            return false;
        }

        for (int i = 0; i < _raycastHits.Count; i++)
        {
            ARRaycastHit hit = _raycastHits[i];

            if (hit.trackable is ARPlane plane)
            {
                if (plane.trackingState == TrackingState.None)
                {
                    continue;
                }

                bool isHorizontal = plane.alignment == PlaneAlignment.HorizontalUp ||
                                    plane.alignment == PlaneAlignment.HorizontalDown;
                if (!isHorizontal)
                {
                    continue;
                }

                hitPose = hit.pose;
                return true;
            }

            // Fallback defensivo si el subsistema nativo reporta el impacto antes de resolver ARPlane
            if (Vector3.Dot(hit.pose.up, Vector3.up) > 0.7f)
            {
                hitPose = hit.pose;
                return true;
            }
        }

        return false;
    }

    private bool IsScreenPointOverUI(Vector2 screenPosition)
    {
        EventSystem currentEventSystem = EventSystem.current;
        if (currentEventSystem == null)
        {
            return false;
        }

        if (_cachedPointerEventData == null || _cachedEventSystem != currentEventSystem)
        {
            _cachedEventSystem = currentEventSystem;
            _cachedPointerEventData = new PointerEventData(currentEventSystem);
        }

        _cachedPointerEventData.Reset();
        _cachedPointerEventData.position = screenPosition;

        _uiRaycastResults.Clear();
        currentEventSystem.RaycastAll(_cachedPointerEventData, _uiRaycastResults);
        return _uiRaycastResults.Count > 0;
    }

    private bool HasAnyTrackedHorizontalPlane()
    {
        if (planeManager == null)
        {
            return false;
        }

        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane == null || plane.trackingState == TrackingState.None || plane.subsumedBy != null)
            {
                continue;
            }

            if (plane.alignment == PlaneAlignment.HorizontalUp ||
                plane.alignment == PlaneAlignment.HorizontalDown)
            {
                return true;
            }
        }

        return false;
    }

    private void EvaluateAndBroadcastState(bool forceNotify)
    {
        ARPlacementState nextState;

        if (_spawnedObject != null)
        {
            nextState = ARPlacementState.ObjectActive;
        }
        else if (IsReticleVisible || HasAnyTrackedHorizontalPlane())
        {
            nextState = ARPlacementState.SurfaceDetected;
        }
        else
        {
            nextState = ARPlacementState.Scanning;
        }

        if (forceNotify || nextState != CurrentState)
        {
            CurrentState = nextState;
            OnStateChanged?.Invoke(CurrentState);
        }
    }

    private void CachePrefabObjectMaterials(GameObject targetRoot)
    {
        if (targetRoot == null)
        {
            return;
        }

        Renderer[] allRenderers = targetRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < allRenderers.Length; i++)
        {
            Renderer rend = allRenderers[i];
            if (rend == null)
            {
                continue;
            }

            Material instancedMat = rend.material;
            if (instancedMat == null)
            {
                continue;
            }

            _instancedMaterials.Add(instancedMat);

            // Si el objeto tiene una base decorativa oscura ("PedestalBase"), mantenemos su contraste
            if (!string.Equals(rend.gameObject.name, "PedestalBase", StringComparison.Ordinal))
            {
                _colorTargetMaterials.Add(instancedMat);
            }
        }

        if (_colorTargetMaterials.Count == 0 && _instancedMaterials.Count > 0)
        {
            _colorTargetMaterials.AddRange(_instancedMaterials);
        }
    }

    private void ApplyColorToActiveRenderers(Color color)
    {
        for (int i = 0; i < _colorTargetMaterials.Count; i++)
        {
            Material mat = _colorTargetMaterials[i];
            if (mat == null)
            {
                continue;
            }

            if (mat.HasProperty(BaseColorShaderId))
            {
                mat.SetColor(BaseColorShaderId, color);
            }

            if (mat.HasProperty(ColorShaderId))
            {
                mat.SetColor(ColorShaderId, color);
            }

            mat.color = color;
        }
    }

    private void CleanupInstancedObjectMaterials()
    {
        for (int i = 0; i < _instancedMaterials.Count; i++)
        {
            if (_instancedMaterials[i] != null)
            {
                Destroy(_instancedMaterials[i]);
            }
        }

        _instancedMaterials.Clear();
        _colorTargetMaterials.Clear();
    }

    /// <summary>
    /// Construye un retículo procedural (anillo + retícula central sobre el plano XZ) sin colisionadores.
    /// </summary>
    private void CreateProceduralReticle()
    {
        if (_reticleObject != null)
        {
            return;
        }

        _reticleObject = new GameObject("AR Placement Reticle");
        MeshFilter meshFilter = _reticleObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = _reticleObject.AddComponent<MeshRenderer>();

        _reticleMesh = GenerateReticleRingMesh(reticleInnerRadius, reticleOuterRadius, 48);
        meshFilter.sharedMesh = _reticleMesh;

        if (reticleMaterial != null)
        {
            _runtimeReticleMaterial = new Material(reticleMaterial);
        }
        else
        {
            _runtimeReticleMaterial = CreateRuntimeTransparentMaterial(new Color(0.20f, 0.90f, 1.0f, 0.88f));
        }

        meshRenderer.sharedMaterial = _runtimeReticleMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        _reticleObject.SetActive(false);
    }

    private static Mesh GenerateReticleRingMesh(float innerRadius, float outerRadius, int segments)
    {
        int clampedSegments = Mathf.Max(16, segments);
        float safeOuter = Mathf.Max(0.02f, outerRadius);
        float safeInner = Mathf.Clamp(innerRadius, 0.005f, safeOuter - 0.004f);

        // Anillo principal + punto central romboidal elevado 2.5 mm para evitar Z-fighting con el plano AR
        const float yOffset = 0.0025f;
        int ringVertexCount = clampedSegments * 2;
        int centerDotVertices = 5;
        Vector3[] vertices = new Vector3[ringVertexCount + centerDotVertices];
        Vector2[] uvs = new Vector2[vertices.Length];
        int[] triangles = new int[clampedSegments * 6 + 4 * 3];

        float angleStep = (Mathf.PI * 2f) / clampedSegments;
        for (int i = 0; i < clampedSegments; i++)
        {
            float angle = i * angleStep;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            int innerIdx = i * 2;
            int outerIdx = innerIdx + 1;

            vertices[innerIdx] = new Vector3(cos * safeInner, yOffset, sin * safeInner);
            vertices[outerIdx] = new Vector3(cos * safeOuter, yOffset, sin * safeOuter);

            uvs[innerIdx] = new Vector2((float)i / clampedSegments, 0f);
            uvs[outerIdx] = new Vector2((float)i / clampedSegments, 1f);

            int nextInner = ((i + 1) % clampedSegments) * 2;
            int nextOuter = nextInner + 1;

            int triBase = i * 6;
            // Caras orientadas hacia arriba (+Y) con orden horario en Unity
            triangles[triBase + 0] = innerIdx;
            triangles[triBase + 1] = nextOuter;
            triangles[triBase + 2] = outerIdx;

            triangles[triBase + 3] = innerIdx;
            triangles[triBase + 4] = nextInner;
            triangles[triBase + 5] = nextOuter;
        }

        float dotRadius = safeInner * 0.22f;
        int centerIdx = ringVertexCount;
        vertices[centerIdx + 0] = new Vector3(0f, yOffset, 0f);
        vertices[centerIdx + 1] = new Vector3(0f, yOffset, dotRadius);
        vertices[centerIdx + 2] = new Vector3(dotRadius, yOffset, 0f);
        vertices[centerIdx + 3] = new Vector3(0f, yOffset, -dotRadius);
        vertices[centerIdx + 4] = new Vector3(-dotRadius, yOffset, 0f);

        for (int k = 0; k < 5; k++)
        {
            uvs[centerIdx + k] = new Vector2(0.5f, 0.5f);
        }

        int dotTriOffset = clampedSegments * 6;
        for (int d = 0; d < 4; d++)
        {
            int currentPerimeter = centerIdx + 1 + d;
            int nextPerimeter = centerIdx + 1 + ((d + 1) % 4);
            triangles[dotTriOffset + d * 3 + 0] = centerIdx;
            triangles[dotTriOffset + d * 3 + 1] = currentPerimeter;
            triangles[dotTriOffset + d * 3 + 2] = nextPerimeter;
        }

        Mesh mesh = new Mesh
        {
            name = "ProceduralARReticleMesh"
        };
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Construye un objeto 3D interactivo procedural con pivote en la base (y = 0)
    /// como respaldo si no se asignó un prefab externo.
    /// </summary>
    private GameObject BuildProceduralInteractiveObject(Vector3 position, Quaternion rotation)
    {
        GameObject root = new GameObject("AR Interactive Object");
        root.transform.SetPositionAndRotation(position, rotation);

        Material bodyMat = defaultObjectMaterial != null
            ? new Material(defaultObjectMaterial)
            : CreateRuntimeLitMaterial(_currentColor);

        Material baseMat = CreateRuntimeLitMaterial(new Color(0.16f, 0.18f, 0.22f, 1.0f));

        // Pedestal cilíndrico apoyado exactamente sobre el plano (pivote en y = 0)
        GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pedestal.name = "PedestalBase";
        pedestal.transform.SetParent(root.transform, false);
        pedestal.transform.localPosition = new Vector3(0f, 0.015f, 0f);
        pedestal.transform.localScale = new Vector3(0.16f, 0.015f, 0.16f);
        Renderer pedestalRenderer = pedestal.GetComponent<Renderer>();
        if (pedestalRenderer != null)
        {
            pedestalRenderer.sharedMaterial = baseMat;
        }

        // Cuerpo principal biselado/rotado sobre el pedestal
        GameObject mainBody = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mainBody.name = "MainBody";
        mainBody.transform.SetParent(root.transform, false);
        mainBody.transform.localPosition = new Vector3(0f, 0.105f, 0f);
        mainBody.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        mainBody.transform.localScale = new Vector3(0.12f, 0.14f, 0.12f);
        Renderer bodyRenderer = mainBody.GetComponent<Renderer>();
        if (bodyRenderer != null)
        {
            bodyRenderer.sharedMaterial = bodyMat;
        }

        // Esfera superior de orientación frontal/corona
        GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        crown.name = "CrownAccent";
        crown.transform.SetParent(root.transform, false);
        crown.transform.localPosition = new Vector3(0f, 0.21f, 0f);
        crown.transform.localScale = new Vector3(0.065f, 0.065f, 0.065f);
        Renderer crownRenderer = crown.GetComponent<Renderer>();
        if (crownRenderer != null)
        {
            crownRenderer.sharedMaterial = bodyMat;
        }

        // Indicador frontal para apreciar visualmente la rotación en eje Y
        GameObject frontFin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        frontFin.name = "FrontIndicator";
        frontFin.transform.SetParent(root.transform, false);
        frontFin.transform.localPosition = new Vector3(0f, 0.105f, 0.072f);
        frontFin.transform.localScale = new Vector3(0.035f, 0.08f, 0.035f);
        Renderer finRenderer = frontFin.GetComponent<Renderer>();
        if (finRenderer != null)
        {
            finRenderer.sharedMaterial = bodyMat;
        }

        _instancedMaterials.Add(baseMat);
        _instancedMaterials.Add(bodyMat);
        _colorTargetMaterials.Add(bodyMat);
        return root;
    }

    private static Material CreateRuntimeTransparentMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        Material mat;
        if (shader != null)
        {
            mat = new Material(shader);
        }
        else
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Renderer tempRend = temp.GetComponent<Renderer>();
            mat = new Material(tempRend.sharedMaterial);
            Destroy(temp);
        }

        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        if (mat.HasProperty(BaseColorShaderId))
        {
            mat.SetColor(BaseColorShaderId, color);
        }

        if (mat.HasProperty(ColorShaderId))
        {
            mat.SetColor(ColorShaderId, color);
        }

        mat.color = color;
        return mat;
    }

    private static Material CreateRuntimeLitMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        Material mat;

        if (shader != null)
        {
            mat = new Material(shader);
        }
        else
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Renderer tempRend = temp.GetComponent<Renderer>();
            mat = new Material(tempRend.sharedMaterial);
            Destroy(temp);
        }

        if (mat.HasProperty("_Smoothness"))
        {
            mat.SetFloat("_Smoothness", 0.65f);
        }

        if (mat.HasProperty("_Metallic"))
        {
            mat.SetFloat("_Metallic", 0.15f);
        }

        if (mat.HasProperty(BaseColorShaderId))
        {
            mat.SetColor(BaseColorShaderId, color);
        }

        if (mat.HasProperty(ColorShaderId))
        {
            mat.SetColor(ColorShaderId, color);
        }

        mat.color = color;
        return mat;
    }
}
