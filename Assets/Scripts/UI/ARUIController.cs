using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Controlador de interfaz de usuario en pantalla para el prototipo de Realidad Aumentada.
/// Conecta controles uGUI (escala, rotación, paleta de colores, reinicio y panel de estado)
/// con <see cref="ARPlacementManager"/> de forma desacoplada y defensiva.
/// </summary>
[DisallowMultipleComponent]
public sealed class ARUIController : MonoBehaviour
{
    public const string StatusScanningMessage = "Escaneando habitación en busca de superficies...";
    public const string StatusSurfaceDetectedMessage = "Superficie detectada: Toca para colocar el objeto";
    public const string StatusObjectActiveMessage = "Objeto activo: Usa los controles inferiores";

    private static readonly Color PresetRedColor = new Color(0.90f, 0.22f, 0.21f, 1.0f);
    private static readonly Color PresetGreenColor = new Color(0.18f, 0.80f, 0.44f, 1.0f);
    private static readonly Color PresetBlueColor = new Color(0.16f, 0.50f, 0.95f, 1.0f);
    private static readonly Color PresetGoldColor = new Color(1.00f, 0.78f, 0.16f, 1.0f);

    private static readonly Color DotScanningColor = new Color(1.00f, 0.72f, 0.18f, 1.0f);
    private static readonly Color DotSurfaceColor = new Color(0.20f, 0.88f, 0.96f, 1.0f);
    private static readonly Color DotActiveColor = new Color(0.22f, 0.90f, 0.48f, 1.0f);

    [Header("Controlador AR")]
    [SerializeField] private ARPlacementManager placementManager;

    [Header("Panel Informativo de Estado")]
    [SerializeField] private Text statusText;
    [SerializeField] private Image statusIndicatorImage;

    [Header("Controles de Escala")]
    [SerializeField] private Slider scaleSlider;
    [SerializeField] private Text scaleValueText;

    [Header("Controles de Rotación (-45° / +45° y Continuo)")]
    [SerializeField] private Button rotateLeftButton;
    [SerializeField] private Button rotateRightButton;
    [SerializeField] private Slider rotationSlider;
    [SerializeField] private float stepRotationDegrees = 45f;
    [SerializeField] private float continuousRotationSpeed = 110f;

    [Header("Botones de Color")]
    [SerializeField] private Button redColorButton;
    [SerializeField] private Button greenColorButton;
    [SerializeField] private Button blueColorButton;
    [SerializeField] private Button goldColorButton;

    [Header("Acción de Limpieza")]
    [SerializeField] private Button resetButton;
    [SerializeField] private CanvasGroup bottomControlsGroup;

    private float _previousRotationSliderValue;
    private int _holdRotationDirection;
    private float _holdElapsed;
    private bool _didContinuousRotateDuringHold;

    private void Awake()
    {
        ResolvePlacementManager();
        ConfigureScaleSliderBounds();
        SetupContinuousRotationHoldHandlers();
    }

    private void OnEnable()
    {
        ResolvePlacementManager();
        RegisterUIListeners();

        if (placementManager != null)
        {
            placementManager.OnStateChanged += HandlePlacementStateChanged;
            placementManager.OnScaleChanged += HandleManagerScaleChanged;

            HandleManagerScaleChanged(placementManager.CurrentScaleFactor);
            HandlePlacementStateChanged(placementManager.CurrentState);
        }
        else
        {
            UpdateStatusDisplay(ARPlacementState.Scanning);
        }
    }

    private void OnDisable()
    {
        UnregisterUIListeners();

        if (placementManager != null)
        {
            placementManager.OnStateChanged -= HandlePlacementStateChanged;
            placementManager.OnScaleChanged -= HandleManagerScaleChanged;
        }

        _holdRotationDirection = 0;
        _holdElapsed = 0f;
        _didContinuousRotateDuringHold = false;
    }

    private void Update()
    {
        if (_holdRotationDirection == 0 || placementManager == null)
        {
            return;
        }

        _holdElapsed += Time.unscaledDeltaTime;
        // Tras una breve pulsación sostenida (0.25 s), rota de forma continua y fluida
        if (_holdElapsed >= 0.25f)
        {
            _didContinuousRotateDuringHold = true;
            float deltaDegrees = _holdRotationDirection * continuousRotationSpeed * Time.unscaledDeltaTime;
            placementManager.RotateY(deltaDegrees);
        }
    }

    private void ResolvePlacementManager()
    {
        if (placementManager == null)
        {
            placementManager = FindAnyObjectByType<ARPlacementManager>();
        }
    }

    private void ConfigureScaleSliderBounds()
    {
        if (scaleSlider == null)
        {
            return;
        }

        scaleSlider.minValue = ARPlacementManager.MinScaleFactor;
        scaleSlider.maxValue = ARPlacementManager.MaxScaleFactor;
        scaleSlider.wholeNumbers = false;

        float initialScale = placementManager != null ? placementManager.CurrentScaleFactor : 1.0f;
        scaleSlider.SetValueWithoutNotify(Mathf.Clamp(initialScale, ARPlacementManager.MinScaleFactor, ARPlacementManager.MaxScaleFactor));
        UpdateScaleLabel(scaleSlider.value);
    }

    private void RegisterUIListeners()
    {
        if (scaleSlider != null)
        {
            scaleSlider.onValueChanged.AddListener(OnScaleSliderValueChanged);
        }

        if (rotateLeftButton != null)
        {
            rotateLeftButton.onClick.AddListener(OnRotateLeftClicked);
        }

        if (rotateRightButton != null)
        {
            rotateRightButton.onClick.AddListener(OnRotateRightClicked);
        }

        if (rotationSlider != null)
        {
            _previousRotationSliderValue = rotationSlider.value;
            rotationSlider.onValueChanged.AddListener(OnRotationSliderValueChanged);
        }

        if (redColorButton != null)
        {
            redColorButton.onClick.AddListener(OnRedColorClicked);
        }

        if (greenColorButton != null)
        {
            greenColorButton.onClick.AddListener(OnGreenColorClicked);
        }

        if (blueColorButton != null)
        {
            blueColorButton.onClick.AddListener(OnBlueColorClicked);
        }

        if (goldColorButton != null)
        {
            goldColorButton.onClick.AddListener(OnGoldColorClicked);
        }

        if (resetButton != null)
        {
            resetButton.onClick.AddListener(OnResetClicked);
        }
    }

    private void UnregisterUIListeners()
    {
        if (scaleSlider != null)
        {
            scaleSlider.onValueChanged.RemoveListener(OnScaleSliderValueChanged);
        }

        if (rotateLeftButton != null)
        {
            rotateLeftButton.onClick.RemoveListener(OnRotateLeftClicked);
        }

        if (rotateRightButton != null)
        {
            rotateRightButton.onClick.RemoveListener(OnRotateRightClicked);
        }

        if (rotationSlider != null)
        {
            rotationSlider.onValueChanged.RemoveListener(OnRotationSliderValueChanged);
        }

        if (redColorButton != null)
        {
            redColorButton.onClick.RemoveListener(OnRedColorClicked);
        }

        if (greenColorButton != null)
        {
            greenColorButton.onClick.RemoveListener(OnGreenColorClicked);
        }

        if (blueColorButton != null)
        {
            blueColorButton.onClick.RemoveListener(OnBlueColorClicked);
        }

        if (goldColorButton != null)
        {
            goldColorButton.onClick.RemoveListener(OnGoldColorClicked);
        }

        if (resetButton != null)
        {
            resetButton.onClick.RemoveListener(OnResetClicked);
        }
    }

    private void SetupContinuousRotationHoldHandlers()
    {
        AttachHoldTrigger(rotateLeftButton, -1);
        AttachHoldTrigger(rotateRightButton, 1);
    }

    private void AttachHoldTrigger(Button button, int direction)
    {
        if (button == null)
        {
            return;
        }

        EventTrigger trigger = button.GetComponent<EventTrigger>();
        if (trigger == null)
        {
            trigger = button.gameObject.AddComponent<EventTrigger>();
        }

        trigger.triggers.Clear();

        EventTrigger.Entry pointerDown = new EventTrigger.Entry
        {
            eventID = EventTriggerType.PointerDown
        };
        pointerDown.callback.AddListener(_ => BeginRotationHold(direction));
        trigger.triggers.Add(pointerDown);

        EventTrigger.Entry pointerUp = new EventTrigger.Entry
        {
            eventID = EventTriggerType.PointerUp
        };
        pointerUp.callback.AddListener(_ => EndRotationHold());
        trigger.triggers.Add(pointerUp);

        EventTrigger.Entry pointerExit = new EventTrigger.Entry
        {
            eventID = EventTriggerType.PointerExit
        };
        pointerExit.callback.AddListener(_ => EndRotationHold());
        trigger.triggers.Add(pointerExit);
    }

    private void BeginRotationHold(int direction)
    {
        _holdRotationDirection = direction;
        _holdElapsed = 0f;
        _didContinuousRotateDuringHold = false;
    }

    private void EndRotationHold()
    {
        _holdRotationDirection = 0;
        _holdElapsed = 0f;
    }

    private void OnScaleSliderValueChanged(float value)
    {
        UpdateScaleLabel(value);

        if (placementManager != null)
        {
            placementManager.SetScale(value);
        }
    }

    private void OnRotateLeftClicked()
    {
        if (_didContinuousRotateDuringHold)
        {
            _didContinuousRotateDuringHold = false;
            return;
        }

        if (placementManager != null)
        {
            placementManager.RotateY(-Mathf.Abs(stepRotationDegrees));
        }
    }

    private void OnRotateRightClicked()
    {
        if (_didContinuousRotateDuringHold)
        {
            _didContinuousRotateDuringHold = false;
            return;
        }

        if (placementManager != null)
        {
            placementManager.RotateY(Mathf.Abs(stepRotationDegrees));
        }
    }

    private void OnRotationSliderValueChanged(float currentDegrees)
    {
        float delta = currentDegrees - _previousRotationSliderValue;
        _previousRotationSliderValue = currentDegrees;

        if (placementManager != null)
        {
            placementManager.RotateY(delta);
        }
    }

    private void OnRedColorClicked()
    {
        if (placementManager != null)
        {
            placementManager.ChangeColor(PresetRedColor);
        }
    }

    private void OnGreenColorClicked()
    {
        if (placementManager != null)
        {
            placementManager.ChangeColor(PresetGreenColor);
        }
    }

    private void OnBlueColorClicked()
    {
        if (placementManager != null)
        {
            placementManager.ChangeColor(PresetBlueColor);
        }
    }

    private void OnGoldColorClicked()
    {
        if (placementManager != null)
        {
            placementManager.ChangeColor(PresetGoldColor);
        }
    }

    private void OnResetClicked()
    {
        if (rotationSlider != null)
        {
            rotationSlider.SetValueWithoutNotify(0f);
            _previousRotationSliderValue = 0f;
        }

        if (placementManager != null)
        {
            placementManager.ResetPlacement();
        }
    }

    private void HandleManagerScaleChanged(float scaleFactor)
    {
        if (scaleSlider != null)
        {
            scaleSlider.SetValueWithoutNotify(scaleFactor);
        }

        UpdateScaleLabel(scaleFactor);
    }

    private void HandlePlacementStateChanged(ARPlacementState state)
    {
        UpdateStatusDisplay(state);

        if (bottomControlsGroup != null)
        {
            bool hasObject = state == ARPlacementState.ObjectActive;
            bottomControlsGroup.alpha = hasObject ? 1.0f : 0.72f;
            bottomControlsGroup.interactable = true;
            bottomControlsGroup.blocksRaycasts = true;
        }
    }

    private void UpdateStatusDisplay(ARPlacementState state)
    {
        string message;
        Color indicatorColor;

        switch (state)
        {
            case ARPlacementState.SurfaceDetected:
                message = StatusSurfaceDetectedMessage;
                indicatorColor = DotSurfaceColor;
                break;

            case ARPlacementState.ObjectActive:
                message = StatusObjectActiveMessage;
                indicatorColor = DotActiveColor;
                break;

            case ARPlacementState.Scanning:
            default:
                message = StatusScanningMessage;
                indicatorColor = DotScanningColor;
                break;
        }

        if (statusText != null)
        {
            statusText.text = message;
        }

        if (statusIndicatorImage != null)
        {
            statusIndicatorImage.color = indicatorColor;
        }
    }

    private void UpdateScaleLabel(float scaleFactor)
    {
        if (scaleValueText != null)
        {
            scaleValueText.text = $"{scaleFactor:0.0}x";
        }
    }
}
