#if UNITY_EDITOR
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Herramienta de Editor para construir de forma 100% procedural y determinista
/// toda la escena de Realidad Aumentada (XR Origin, AR Session, visualizador de planos,
/// materiales URP, objeto interactivo 3D y Canvas UI 1080x1920 completamente conectado).
/// </summary>
public static class ARSceneAutomator
{
    private const string MaterialsFolder = "Assets/Materials";
    private const string PrefabsFolder = "Assets/Prefabs";

    private const string PlaneMaterialPath = MaterialsFolder + "/ARPlaneSemitransparent.mat";
    private const string PlaneLineMaterialPath = MaterialsFolder + "/ARPlaneBoundaryLine.mat";
    private const string ReticleMaterialPath = MaterialsFolder + "/ARReticleMaterial.mat";
    private const string ObjectBodyMaterialPath = MaterialsFolder + "/ARObjectBody.mat";
    private const string ObjectBaseMaterialPath = MaterialsFolder + "/ARObjectPedestal.mat";

    private const string PlanePrefabPath = PrefabsFolder + "/ARPlaneVisualizer.prefab";
    private const string InteractiveObjectPrefabPath = PrefabsFolder + "/ARInteractiveObject.prefab";

    private static readonly int BaseColorShaderId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorShaderId = Shader.PropertyToID("_Color");

    [MenuItem("AR Tools/Construir Escena AR")]
    public static void BuildARScene()
    {
        EnsureFolderExists("Assets", "Materials");
        EnsureFolderExists("Assets", "Prefabs");

        EnsureIOSCameraPermissionDescription();
        EnsureURPARBackgroundRendererFeature();

        Scene activeScene = EditorSceneManager.GetActiveScene();
        ClearSceneRootObjects(activeScene);

        // 1. Materiales y Prefabs base (compatibles con URP e iOS sin pérdida por shader stripping)
        Material planeMat = CreateOrUpdateTransparentMaterial(
            PlaneMaterialPath,
            new Color(0.12f, 0.82f, 0.95f, 0.28f),
            useLitShader: false);

        Material planeLineMat = CreateOrUpdateTransparentMaterial(
            PlaneLineMaterialPath,
            new Color(0.25f, 0.94f, 1.00f, 0.85f),
            useLitShader: false);

        Material reticleMat = CreateOrUpdateTransparentMaterial(
            ReticleMaterialPath,
            new Color(0.20f, 0.92f, 1.00f, 0.90f),
            useLitShader: false);

        Material objectBodyMat = CreateOrUpdateOpaqueLitMaterial(
            ObjectBodyMaterialPath,
            new Color(0.16f, 0.50f, 0.95f, 1.00f),
            metallic: 0.18f,
            smoothness: 0.68f);

        Material objectBaseMat = CreateOrUpdateOpaqueLitMaterial(
            ObjectBaseMaterialPath,
            new Color(0.15f, 0.17f, 0.22f, 1.00f),
            metallic: 0.35f,
            smoothness: 0.48f);

        GameObject planeVisualizerPrefab = CreateOrUpdatePlaneVisualizerPrefab(planeMat, planeLineMat);
        GameObject interactiveObjectPrefab = CreateOrUpdateInteractiveObjectPrefab(objectBodyMat, objectBaseMat);

        // 2. Iluminación direccional para realzar volúmenes 3D
        CreateDirectionalLight();

        // 3. AR Session + ARInputManager
        CreateARSessionHierarchy();

        // 4. XR Origin + Cámara AR + ARPlaneManager (Horizontal) + ARRaycastManager + ARPlacementManager
        ARPlacementManager placementManager = CreateXROriginHierarchy(
            planeVisualizerPrefab,
            interactiveObjectPrefab,
            reticleMat,
            objectBodyMat);

        // 5. EventSystem compatible con el Input System moderno (iOS Touch + Editor)
        CreateEventSystemHierarchy();

        // 6. Canvas UI (1080x1920) con todos los controles y referencias serializadas
        CreateUICanvasHierarchy(placementManager);

        EditorSceneManager.MarkSceneDirty(activeScene);
        AssetDatabase.SaveAssets();

        Selection.activeGameObject = placementManager != null ? placementManager.gameObject : null;
        Debug.Log("[ARSceneAutomator] Escena AR construida y configurada exitosamente para Unity 6 + iOS (ARKit).");
    }

    private static void ClearSceneRootObjects(Scene scene)
    {
        if (!scene.IsValid())
        {
            return;
        }

        GameObject[] rootObjects = scene.GetRootGameObjects();
        for (int i = rootObjects.Length - 1; i >= 0; i--)
        {
            if (rootObjects[i] != null)
            {
                Object.DestroyImmediate(rootObjects[i]);
            }
        }
    }

    private static void CreateDirectionalLight()
    {
        GameObject lightGo = new GameObject("Directional Light");
        Light directionalLight = lightGo.AddComponent<Light>();
        directionalLight.type = LightType.Directional;
        directionalLight.color = new Color(1.0f, 0.98f, 0.94f, 1.0f);
        directionalLight.intensity = 1.15f;
        directionalLight.shadows = LightShadows.Soft;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        if (lightGo.GetComponent<UniversalAdditionalLightData>() == null)
        {
            lightGo.AddComponent<UniversalAdditionalLightData>();
        }
    }

    private static GameObject CreateARSessionHierarchy()
    {
        GameObject arSessionGo = new GameObject("AR Session");
        arSessionGo.AddComponent<ARSession>();
        arSessionGo.AddComponent<ARInputManager>();
        return arSessionGo;
    }

    private static ARPlacementManager CreateXROriginHierarchy(
        GameObject planePrefab,
        GameObject spawnablePrefab,
        Material reticleMat,
        Material defaultObjMat)
    {
        GameObject xrOriginGo = new GameObject("XR Origin");
        XROrigin xrOrigin = xrOriginGo.AddComponent<XROrigin>();

        GameObject cameraOffsetGo = new GameObject("Camera Offset");
        cameraOffsetGo.transform.SetParent(xrOriginGo.transform, false);

        GameObject arCameraGo = new GameObject("Main Camera");
        arCameraGo.tag = "MainCamera";
        arCameraGo.transform.SetParent(cameraOffsetGo.transform, false);

        Camera arCamera = arCameraGo.AddComponent<Camera>();
        arCamera.clearFlags = CameraClearFlags.SolidColor;
        arCamera.backgroundColor = Color.black;
        arCamera.nearClipPlane = 0.05f;
        arCamera.farClipPlane = 25f;

        arCameraGo.AddComponent<AudioListener>();

        ARCameraManager cameraManager = arCameraGo.AddComponent<ARCameraManager>();
        cameraManager.autoFocusRequested = true;

        arCameraGo.AddComponent<ARCameraBackground>();

        if (arCameraGo.GetComponent<UniversalAdditionalCameraData>() == null)
        {
            arCameraGo.AddComponent<UniversalAdditionalCameraData>();
        }

        TrackedPoseDriver trackedPoseDriver = arCameraGo.AddComponent<TrackedPoseDriver>();
        ConfigureTrackedPoseDriver(trackedPoseDriver);

        xrOrigin.CameraFloorOffsetObject = cameraOffsetGo;
        xrOrigin.Camera = arCamera;

        ARPlaneManager planeManager = xrOriginGo.AddComponent<ARPlaneManager>();
        planeManager.planePrefab = planePrefab;
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        SerializedObject serializedPlaneManager = new SerializedObject(planeManager);
        SerializedProperty planePrefabProp = serializedPlaneManager.FindProperty("m_PlanePrefab");
        if (planePrefabProp != null)
        {
            planePrefabProp.objectReferenceValue = planePrefab;
        }

        SerializedProperty detectionModeProp = serializedPlaneManager.FindProperty("m_DetectionMode");
        if (detectionModeProp != null)
        {
            detectionModeProp.intValue = (int)PlaneDetectionMode.Horizontal;
        }
        serializedPlaneManager.ApplyModifiedPropertiesWithoutUndo();

        ARRaycastManager raycastManager = xrOriginGo.AddComponent<ARRaycastManager>();

        ARPlacementManager placementManager = xrOriginGo.AddComponent<ARPlacementManager>();
        SerializedObject serializedPlacement = new SerializedObject(placementManager);
        SetSerializedReference(serializedPlacement, "xrOrigin", xrOrigin);
        SetSerializedReference(serializedPlacement, "raycastManager", raycastManager);
        SetSerializedReference(serializedPlacement, "planeManager", planeManager);
        SetSerializedReference(serializedPlacement, "arCamera", arCamera);
        SetSerializedReference(serializedPlacement, "spawnablePrefab", spawnablePrefab);
        SetSerializedReference(serializedPlacement, "reticleMaterial", reticleMat);
        SetSerializedReference(serializedPlacement, "defaultObjectMaterial", defaultObjMat);
        serializedPlacement.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(xrOrigin);
        EditorUtility.SetDirty(planeManager);
        EditorUtility.SetDirty(raycastManager);
        EditorUtility.SetDirty(placementManager);

        return placementManager;
    }

    private static void ConfigureTrackedPoseDriver(TrackedPoseDriver trackedPoseDriver)
    {
        if (trackedPoseDriver == null)
        {
            return;
        }

        InputAction positionAction = new InputAction(
            "Position",
            binding: "<XRHMD>/centerEyePosition",
            expectedControlType: "Vector3");
        positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");

        InputAction rotationAction = new InputAction(
            "Rotation",
            binding: "<XRHMD>/centerEyeRotation",
            expectedControlType: "Quaternion");
        rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");

        trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
        trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);
        EditorUtility.SetDirty(trackedPoseDriver);
    }

    private static GameObject CreateOrUpdatePlaneVisualizerPrefab(Material planeMeshMat, Material boundaryLineMat)
    {
        GameObject tempPlaneGo = new GameObject("AR Plane Visualizer");
        tempPlaneGo.AddComponent<ARPlane>();
        tempPlaneGo.AddComponent<ARPlaneMeshVisualizer>();
        tempPlaneGo.AddComponent<MeshFilter>();
        tempPlaneGo.AddComponent<MeshCollider>();

        MeshRenderer meshRenderer = tempPlaneGo.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = planeMeshMat;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        LineRenderer lineRenderer = tempPlaneGo.AddComponent<LineRenderer>();
        lineRenderer.sharedMaterial = boundaryLineMat;
        lineRenderer.useWorldSpace = false;
        lineRenderer.loop = true;
        lineRenderer.widthMultiplier = 0.008f;
        lineRenderer.numCornerVertices = 4;
        lineRenderer.numCapVertices = 4;
        lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.startColor = new Color(0.25f, 0.95f, 1.0f, 0.9f);
        lineRenderer.endColor = new Color(0.25f, 0.95f, 1.0f, 0.9f);

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(tempPlaneGo, PlanePrefabPath);
        Object.DestroyImmediate(tempPlaneGo);
        return savedPrefab;
    }

    private static GameObject CreateOrUpdateInteractiveObjectPrefab(Material bodyMaterial, Material baseMaterial)
    {
        GameObject root = new GameObject("ARInteractiveObject");

        // Base cilíndrica con pivote exacto en y = 0 para apoyar al ras sobre planos horizontales
        GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pedestal.name = "PedestalBase";
        pedestal.transform.SetParent(root.transform, false);
        pedestal.transform.localPosition = new Vector3(0f, 0.015f, 0f);
        pedestal.transform.localScale = new Vector3(0.16f, 0.015f, 0.16f);
        Renderer pedestalRenderer = pedestal.GetComponent<Renderer>();
        if (pedestalRenderer != null)
        {
            pedestalRenderer.sharedMaterial = baseMaterial;
        }

        // Cuerpo geométrico principal
        GameObject mainBody = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mainBody.name = "MainBody";
        mainBody.transform.SetParent(root.transform, false);
        mainBody.transform.localPosition = new Vector3(0f, 0.105f, 0f);
        mainBody.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        mainBody.transform.localScale = new Vector3(0.12f, 0.14f, 0.12f);
        Renderer mainRenderer = mainBody.GetComponent<Renderer>();
        if (mainRenderer != null)
        {
            mainRenderer.sharedMaterial = bodyMaterial;
        }

        // Esfera superior
        GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        crown.name = "CrownAccent";
        crown.transform.SetParent(root.transform, false);
        crown.transform.localPosition = new Vector3(0f, 0.21f, 0f);
        crown.transform.localScale = new Vector3(0.065f, 0.065f, 0.065f);
        Renderer crownRenderer = crown.GetComponent<Renderer>();
        if (crownRenderer != null)
        {
            crownRenderer.sharedMaterial = bodyMaterial;
        }

        // Aleta frontal para visualizar con claridad los giros de RotateY
        GameObject frontIndicator = GameObject.CreatePrimitive(PrimitiveType.Cube);
        frontIndicator.name = "FrontIndicator";
        frontIndicator.transform.SetParent(root.transform, false);
        frontIndicator.transform.localPosition = new Vector3(0f, 0.105f, 0.072f);
        frontIndicator.transform.localScale = new Vector3(0.035f, 0.08f, 0.035f);
        Renderer frontRenderer = frontIndicator.GetComponent<Renderer>();
        if (frontRenderer != null)
        {
            frontRenderer.sharedMaterial = bodyMaterial;
        }

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, InteractiveObjectPrefabPath);
        Object.DestroyImmediate(root);
        return savedPrefab;
    }

    private static void CreateEventSystemHierarchy()
    {
        GameObject eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<InputSystemUIInputModule>();
    }

    private static ARUIController CreateUICanvasHierarchy(ARPlacementManager placementManager)
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            uiLayer = 5;
        }

        Font uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (uiFont == null)
        {
            uiFont = Font.CreateDynamicFontFromOSFont("Arial", 28);
        }

        Sprite standardSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        Sprite backgroundSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        Sprite knobSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

        // Canvas raíz 1080x1920 (Scale With Screen Size)
        GameObject canvasGo = new GameObject("AR UI Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.layer = uiLayer;

        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        ARUIController uiController = canvasGo.AddComponent<ARUIController>();

        // --- PANEL SUPERIOR INFORMATIVO DE ESTADO ---
        GameObject statusPanel = CreateUIPanel(
            "StatusPanel",
            canvasGo.transform,
            backgroundSprite,
            new Color(0.06f, 0.08f, 0.13f, 0.86f));
        RectTransform statusRect = statusPanel.GetComponent<RectTransform>();
        statusRect.anchorMin = new Vector2(0.04f, 1f);
        statusRect.anchorMax = new Vector2(0.96f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0f, -85f);
        statusRect.sizeDelta = new Vector2(0f, 112f);

        GameObject statusDotGo = CreateUIObject("StatusIndicatorDot", statusPanel.transform);
        Image statusDotImage = statusDotGo.AddComponent<Image>();
        statusDotImage.sprite = knobSprite;
        statusDotImage.color = new Color(1.00f, 0.72f, 0.18f, 1.0f);
        statusDotImage.raycastTarget = false;
        RectTransform dotRect = statusDotGo.GetComponent<RectTransform>();
        dotRect.anchorMin = new Vector2(0f, 0.5f);
        dotRect.anchorMax = new Vector2(0f, 0.5f);
        dotRect.pivot = new Vector2(0.5f, 0.5f);
        dotRect.anchoredPosition = new Vector2(44f, 0f);
        dotRect.sizeDelta = new Vector2(26f, 26f);

        Text statusText = CreateUIText(
            "StatusText",
            statusPanel.transform,
            uiFont,
            ARUIController.StatusScanningMessage,
            29,
            FontStyle.Bold,
            Color.white,
            TextAnchor.MiddleLeft);
        statusText.resizeTextForBestFit = true;
        statusText.resizeTextMinSize = 20;
        statusText.resizeTextMaxSize = 31;
        RectTransform statusTextRect = statusText.GetComponent<RectTransform>();
        statusTextRect.anchorMin = Vector2.zero;
        statusTextRect.anchorMax = Vector2.one;
        statusTextRect.offsetMin = new Vector2(78f, 12f);
        statusTextRect.offsetMax = new Vector2(-28f, -12f);

        // --- PANEL INFERIOR DE CONTROLES INTERACTIVOS ---
        GameObject bottomPanel = CreateUIPanel(
            "BottomControlsPanel",
            canvasGo.transform,
            backgroundSprite,
            new Color(0.06f, 0.08f, 0.13f, 0.90f));
        CanvasGroup bottomCanvasGroup = bottomPanel.AddComponent<CanvasGroup>();
        RectTransform bottomRect = bottomPanel.GetComponent<RectTransform>();
        bottomRect.anchorMin = new Vector2(0.04f, 0f);
        bottomRect.anchorMax = new Vector2(0.96f, 0f);
        bottomRect.pivot = new Vector2(0.5f, 0f);
        bottomRect.anchoredPosition = new Vector2(0f, 64f);
        bottomRect.sizeDelta = new Vector2(0f, 476f);

        // Fila 1: Escala (Título + Valor + Slider 0.2x - 3.0x)
        Text scaleTitle = CreateUIText(
            "ScaleTitleLabel",
            bottomPanel.transform,
            uiFont,
            "ESCALA DEL OBJETO",
            24,
            FontStyle.Bold,
            new Color(0.75f, 0.82f, 0.92f, 1f),
            TextAnchor.MiddleLeft);
        RectTransform scaleTitleRect = scaleTitle.GetComponent<RectTransform>();
        scaleTitleRect.anchorMin = new Vector2(0f, 1f);
        scaleTitleRect.anchorMax = new Vector2(0.7f, 1f);
        scaleTitleRect.pivot = new Vector2(0f, 1f);
        scaleTitleRect.offsetMin = new Vector2(36f, -62f);
        scaleTitleRect.offsetMax = new Vector2(0f, -20f);

        Text scaleValueText = CreateUIText(
            "ScaleValueLabel",
            bottomPanel.transform,
            uiFont,
            "1.0x",
            26,
            FontStyle.Bold,
            new Color(0.24f, 0.88f, 0.98f, 1f),
            TextAnchor.MiddleRight);
        RectTransform scaleValRect = scaleValueText.GetComponent<RectTransform>();
        scaleValRect.anchorMin = new Vector2(0.7f, 1f);
        scaleValRect.anchorMax = new Vector2(1f, 1f);
        scaleValRect.pivot = new Vector2(1f, 1f);
        scaleValRect.offsetMin = new Vector2(0f, -62f);
        scaleValRect.offsetMax = new Vector2(-36f, -20f);

        Slider scaleSlider = CreateUISlider(
            "ScaleSlider",
            bottomPanel.transform,
            standardSprite,
            knobSprite,
            ARPlacementManager.MinScaleFactor,
            ARPlacementManager.MaxScaleFactor,
            1.0f);
        RectTransform sliderRect = scaleSlider.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0f, 1f);
        sliderRect.anchorMax = new Vector2(1f, 1f);
        sliderRect.pivot = new Vector2(0.5f, 1f);
        sliderRect.offsetMin = new Vector2(36f, -128f);
        sliderRect.offsetMax = new Vector2(-36f, -68f);

        // Fila 2: Botones de Rotación (-45° / +45° con soporte de pulsación continua)
        Button rotateLeftBtn = CreateUIButton(
            "BtnRotateLeft",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "↺  Rotar -45°",
            27,
            new Color(0.18f, 0.24f, 0.35f, 1f),
            Color.white);
        RectTransform rotLeftRect = rotateLeftBtn.GetComponent<RectTransform>();
        rotLeftRect.anchorMin = new Vector2(0f, 1f);
        rotLeftRect.anchorMax = new Vector2(0.5f, 1f);
        rotLeftRect.offsetMin = new Vector2(36f, -234f);
        rotLeftRect.offsetMax = new Vector2(-12f, -148f);

        Button rotateRightBtn = CreateUIButton(
            "BtnRotateRight",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Rotar +45°  ↻",
            27,
            new Color(0.18f, 0.24f, 0.35f, 1f),
            Color.white);
        RectTransform rotRightRect = rotateRightBtn.GetComponent<RectTransform>();
        rotRightRect.anchorMin = new Vector2(0.5f, 1f);
        rotRightRect.anchorMax = new Vector2(1f, 1f);
        rotRightRect.offsetMin = new Vector2(12f, -234f);
        rotRightRect.offsetMax = new Vector2(-36f, -148f);

        // Fila 3: Botones de Color (Rojo, Verde, Azul, Dorado)
        Button redBtn = CreateUIButton(
            "BtnColorRed",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Rojo",
            25,
            new Color(0.90f, 0.22f, 0.21f, 1f),
            Color.white);
        SetHorizontalRowSlot(redBtn.GetComponent<RectTransform>(), 0, 4, -342f, -254f, 36f, 16f);

        Button greenBtn = CreateUIButton(
            "BtnColorGreen",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Verde",
            25,
            new Color(0.18f, 0.80f, 0.44f, 1f),
            new Color(0.04f, 0.14f, 0.08f, 1f));
        SetHorizontalRowSlot(greenBtn.GetComponent<RectTransform>(), 1, 4, -342f, -254f, 36f, 16f);

        Button blueBtn = CreateUIButton(
            "BtnColorBlue",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Azul",
            25,
            new Color(0.16f, 0.50f, 0.95f, 1f),
            Color.white);
        SetHorizontalRowSlot(blueBtn.GetComponent<RectTransform>(), 2, 4, -342f, -254f, 36f, 16f);

        Button goldBtn = CreateUIButton(
            "BtnColorGold",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Dorado",
            25,
            new Color(1.00f, 0.78f, 0.16f, 1f),
            new Color(0.16f, 0.11f, 0.02f, 1f));
        SetHorizontalRowSlot(goldBtn.GetComponent<RectTransform>(), 3, 4, -342f, -254f, 36f, 16f);

        // Fila 4: Botón Eliminar / Reiniciar
        Button resetBtn = CreateUIButton(
            "BtnReset",
            bottomPanel.transform,
            standardSprite,
            uiFont,
            "Eliminar / Reiniciar",
            28,
            new Color(0.76f, 0.19f, 0.23f, 1f),
            Color.white);
        RectTransform resetRect = resetBtn.GetComponent<RectTransform>();
        resetRect.anchorMin = new Vector2(0f, 1f);
        resetRect.anchorMax = new Vector2(1f, 1f);
        resetRect.offsetMin = new Vector2(36f, -450f);
        resetRect.offsetMax = new Vector2(-36f, -364f);

        // Asignación serializada de referencias en ARUIController
        SerializedObject serializedUI = new SerializedObject(uiController);
        SetSerializedReference(serializedUI, "placementManager", placementManager);
        SetSerializedReference(serializedUI, "statusText", statusText);
        SetSerializedReference(serializedUI, "statusIndicatorImage", statusDotImage);
        SetSerializedReference(serializedUI, "scaleSlider", scaleSlider);
        SetSerializedReference(serializedUI, "scaleValueText", scaleValueText);
        SetSerializedReference(serializedUI, "rotateLeftButton", rotateLeftBtn);
        SetSerializedReference(serializedUI, "rotateRightButton", rotateRightBtn);
        SetSerializedReference(serializedUI, "redColorButton", redBtn);
        SetSerializedReference(serializedUI, "greenColorButton", greenBtn);
        SetSerializedReference(serializedUI, "blueColorButton", blueBtn);
        SetSerializedReference(serializedUI, "goldColorButton", goldBtn);
        SetSerializedReference(serializedUI, "resetButton", resetBtn);
        SetSerializedReference(serializedUI, "bottomControlsGroup", bottomCanvasGroup);
        serializedUI.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(uiController);
        return uiController;
    }

    private static void SetHorizontalRowSlot(
        RectTransform rect,
        int index,
        int totalCount,
        float bottomFromTop,
        float topFromTop,
        float outerPadding,
        float spacing)
    {
        float slice = 1f / totalCount;
        rect.anchorMin = new Vector2(index * slice, 1f);
        rect.anchorMax = new Vector2((index + 1) * slice, 1f);

        float leftInset = index == 0 ? outerPadding : spacing * 0.5f;
        float rightInset = index == totalCount - 1 ? -outerPadding : -spacing * 0.5f;

        rect.offsetMin = new Vector2(leftInset, bottomFromTop);
        rect.offsetMax = new Vector2(rightInset, topFromTop);
    }

    private static GameObject CreateUIObject(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = parent != null ? parent.gameObject.layer : 5;
        if (parent != null)
        {
            go.transform.SetParent(parent, false);
        }
        return go;
    }

    private static GameObject CreateUIPanel(string name, Transform parent, Sprite sprite, Color color)
    {
        GameObject panelGo = CreateUIObject(name, parent);
        Image img = panelGo.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        img.color = color;
        return panelGo;
    }

    private static Text CreateUIText(
        string name,
        Transform parent,
        Font font,
        string content,
        int fontSize,
        FontStyle fontStyle,
        Color color,
        TextAnchor alignment)
    {
        GameObject textGo = CreateUIObject(name, parent);
        Text txt = textGo.AddComponent<Text>();
        txt.font = font;
        txt.text = content;
        txt.fontSize = fontSize;
        txt.fontStyle = fontStyle;
        txt.color = color;
        txt.alignment = alignment;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.raycastTarget = false;
        return txt;
    }

    private static Button CreateUIButton(
        string name,
        Transform parent,
        Sprite sprite,
        Font font,
        string label,
        int fontSize,
        Color backgroundColor,
        Color textColor)
    {
        GameObject btnGo = CreateUIObject(name, parent);
        Image btnImage = btnGo.AddComponent<Image>();
        btnImage.sprite = sprite;
        btnImage.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        btnImage.color = backgroundColor;

        Button button = btnGo.AddComponent<Button>();
        button.targetGraphic = btnImage;

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        colors.pressedColor = new Color(0.74f, 0.74f, 0.74f, 1f);
        colors.selectedColor = Color.white;
        button.colors = colors;

        Text labelText = CreateUIText(
            "Label",
            btnGo.transform,
            font,
            label,
            fontSize,
            FontStyle.Bold,
            textColor,
            TextAnchor.MiddleCenter);
        RectTransform labelRect = labelText.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(8f, 4f);
        labelRect.offsetMax = new Vector2(-8f, -4f);

        return button;
    }

    private static Slider CreateUISlider(
        string name,
        Transform parent,
        Sprite barSprite,
        Sprite knobSprite,
        float minValue,
        float maxValue,
        float initialValue)
    {
        GameObject sliderRoot = CreateUIObject(name, parent);
        Slider slider = sliderRoot.AddComponent<Slider>();

        // Pista de fondo
        GameObject backgroundGo = CreateUIObject("Background", sliderRoot.transform);
        Image bgImage = backgroundGo.AddComponent<Image>();
        bgImage.sprite = barSprite;
        bgImage.type = barSprite != null ? Image.Type.Sliced : Image.Type.Simple;
        bgImage.color = new Color(0.18f, 0.22f, 0.30f, 1f);
        RectTransform bgRect = backgroundGo.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0f, 0.35f);
        bgRect.anchorMax = new Vector2(1f, 0.65f);
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;

        // Área de relleno
        GameObject fillAreaGo = CreateUIObject("Fill Area", sliderRoot.transform);
        RectTransform fillAreaRect = fillAreaGo.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.35f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.65f);
        fillAreaRect.offsetMin = new Vector2(10f, 0f);
        fillAreaRect.offsetMax = new Vector2(-10f, 0f);

        GameObject fillGo = CreateUIObject("Fill", fillAreaGo.transform);
        Image fillImage = fillGo.AddComponent<Image>();
        fillImage.sprite = barSprite;
        fillImage.type = barSprite != null ? Image.Type.Sliced : Image.Type.Simple;
        fillImage.color = new Color(0.20f, 0.84f, 0.96f, 1f);
        RectTransform fillRect = fillGo.GetComponent<RectTransform>();
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        // Área del control deslizante (Handle)
        GameObject handleSlideAreaGo = CreateUIObject("Handle Slide Area", sliderRoot.transform);
        RectTransform handleAreaRect = handleSlideAreaGo.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = new Vector2(20f, 0f);
        handleAreaRect.offsetMax = new Vector2(-20f, 0f);

        GameObject handleGo = CreateUIObject("Handle", handleSlideAreaGo.transform);
        Image handleImage = handleGo.AddComponent<Image>();
        handleImage.sprite = knobSprite;
        handleImage.color = Color.white;
        RectTransform handleRect = handleGo.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(52f, 52f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.targetGraphic = handleImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = minValue;
        slider.maxValue = maxValue;
        slider.wholeNumbers = false;
        slider.value = initialValue;

        return slider;
    }

    private static Material CreateOrUpdateTransparentMaterial(string assetPath, Color color, bool useLitShader)
    {
        Shader shader = Shader.Find(useLitShader
            ? "Universal Render Pipeline/Lit"
            : "Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, assetPath);
        }
        else if (shader != null && mat.shader != shader)
        {
            mat.shader = shader;
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
            mat.SetShaderPassEnabled("DepthOnly", false);
            mat.SetShaderPassEnabled("SHADOWCASTER", false);
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
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Material CreateOrUpdateOpaqueLitMaterial(
        string assetPath,
        Color color,
        float metallic,
        float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, assetPath);
        }
        else if (shader != null && mat.shader != shader)
        {
            mat.shader = shader;
        }

        if (mat.HasProperty("_Metallic"))
        {
            mat.SetFloat("_Metallic", metallic);
        }

        if (mat.HasProperty("_Smoothness"))
        {
            mat.SetFloat("_Smoothness", smoothness);
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
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// Garantiza que todos los ScriptableRendererData de URP incluyan ARBackgroundRendererFeature
    /// para que el feed de cámara ARKit se renderice correctamente en dispositivos iOS.
    /// </summary>
    private static void EnsureURPARBackgroundRendererFeature()
    {
        string[] rendererGuids = AssetDatabase.FindAssets("t:ScriptableRendererData", new[] { "Assets" });
        for (int i = 0; i < rendererGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(rendererGuids[i]);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.Ordinal))
            {
                continue;
            }

            ScriptableRendererData rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (rendererData == null)
            {
                continue;
            }

            bool hasARBackgroundFeature = false;
            for (int f = 0; f < rendererData.rendererFeatures.Count; f++)
            {
                if (rendererData.rendererFeatures[f] is ARBackgroundRendererFeature)
                {
                    hasARBackgroundFeature = true;
                    break;
                }
            }

            if (hasARBackgroundFeature)
            {
                continue;
            }

            ARBackgroundRendererFeature arFeature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
            arFeature.name = nameof(ARBackgroundRendererFeature);
            AssetDatabase.AddObjectToAsset(arFeature, rendererData);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(arFeature, out _, out long localId);

            SerializedObject serializedRenderer = new SerializedObject(rendererData);
            SerializedProperty featuresProp = serializedRenderer.FindProperty("m_RendererFeatures");
            SerializedProperty mapProp = serializedRenderer.FindProperty("m_RendererFeatureMap");

            if (featuresProp != null)
            {
                int newIdx = featuresProp.arraySize;
                featuresProp.InsertArrayElementAtIndex(newIdx);
                featuresProp.GetArrayElementAtIndex(newIdx).objectReferenceValue = arFeature;
            }

            if (mapProp != null)
            {
                int newMapIdx = mapProp.arraySize;
                mapProp.InsertArrayElementAtIndex(newMapIdx);
                mapProp.GetArrayElementAtIndex(newMapIdx).longValue = localId;
            }

            serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(arFeature);
            EditorUtility.SetDirty(rendererData);
        }
    }

    /// <summary>
    /// Asegura que NSCameraUsageDescription esté definido para evitar cierres forzados al abrir en iOS.
    /// </summary>
    private static void EnsureIOSCameraPermissionDescription()
    {
        if (string.IsNullOrWhiteSpace(PlayerSettings.iOS.cameraUsageDescription))
        {
            PlayerSettings.iOS.cameraUsageDescription =
                "Se requiere acceso a la cámara para detectar superficies y proyectar objetos en Realidad Aumentada.";
        }
    }

    private static void EnsureFolderExists(string parentFolder, string folderName)
    {
        string combinedPath = parentFolder + "/" + folderName;
        if (!AssetDatabase.IsValidFolder(combinedPath))
        {
            AssetDatabase.CreateFolder(parentFolder, folderName);
        }
    }

    private static void SetSerializedReference(SerializedObject target, string propertyName, Object referenceValue)
    {
        if (target == null)
        {
            return;
        }

        SerializedProperty prop = target.FindProperty(propertyName);
        if (prop != null)
        {
            prop.objectReferenceValue = referenceValue;
        }
    }
}
#endif
