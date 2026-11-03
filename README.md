# Aplicación de Realidad Aumentada (AR) - Prototipo Interactivo

Prototipo funcional de Realidad Aumentada desarrollado en **Unity 6 (6000.4.1f1)** empleando **AR Foundation** y **Apple ARKit XR Plugin**. La aplicación demuestra el reconocimiento espacial de superficies físicas, el anclaje matemático de elementos tridimensionales mediante proyección de rayos y la manipulación en tiempo real a través de una interfaz gráfica de usuario.

---

## 📌 Demostración en Video
- **Video Demostrativo (YouTube):** (https://youtu.be/8qL3Vsck5rU)

---

## 🎯 Características del Proyecto

### 1. Percepción y Detección del Entorno
- **Detección de Planos Horizontales:** Rastreo dinámico de superficies del mundo real asistido por los subsistemas de AR Foundation (`ARPlaneManager`).
- **Retículo de Posicionamiento:** Indicador procedural proyectado sobre la superficie detectada que visualiza el punto exacto de colisión en tiempo real antes de la interacción.

### 2. Spawneo y Manipulación Espacial
- **Colocación por Raycasting:** Interacción táctil (*Touch / Pointer*) que dispara rayos espaciales (`ARRaycastManager`) contra polígonos de planos detectados (`TrackableType.PlaneWithinPolygon`) para instanciar o reposicionar suavemente el objeto 3D interactivo.
- **Control de Escala Proporcional:** Modificación dinámica de la escala local del elemento dentro de un rango calibrado (0.2x a 3.0x).
- **Rotación en Eje Y:** Reorientación incremental (+45° / -45°) alrededor de la normal del plano.
- **Modificación de Materiales en Caliente:** Alteración de las propiedades de color del `MeshRenderer` (Rojo, Verde, Azul, Dorado) sin fugas de memoria por duplicación de instancias.

### 3. Interfaz de Usuario (UI) y Feedback Dinámico
- **Máquina de Estados Visual:** Panel superior con notificaciones contextuales en vivo según la fase del tracking:
  - *Buscando superficies...*
  - *Superficie detectada: Toca para colocar el objeto*
  - *Objeto activo: Usa los controles inferiores*
- **Panel de Control Inferior:** Sliders y botones táctiles anclados con diseño responsivo uGUI.
- **Reinicio de Escena:** Función para destruir la instancia activa, restablecer los parámetros de manipulación y reanudar el rastreo.

---

## 🛠️ Especificaciones Técnicas

| Componente | Especificación / Versión |
| :--- | :--- |
| **Motor Gráfico** | Unity 6 (6000.4.1f1) |
| **Arquitectura de AR** | AR Foundation 6.4.3 |
| **Plataforma Target** | iOS 14.0+ / Apple ARKit XR Plugin 6.4.3 |
| **Lenguaje de Scripting**| C# 9.0+ / .NET Standard 2.1 |
| **Sistema de UI** | Unity UI (uGUI) |
| **Entorno de Simulación** | Unity XR Simulation (XR Environment Suite) |

---

## 🏗️ Software

El proyecto se estructura bajo un esquema desacoplado y modular:

- `Assets/Scripts/AR/ARPlacementManager.cs`: Administra la lógica de proyección de rayos, el retículo de seguimiento, el ciclo de vida del objeto instanciado y sus transformaciones espaciales.
- `Assets/Scripts/UI/ARUIController.cs`: Maneja los eventos de interacción de la UI (sliders, botones) y coordina la actualización del texto de estado según los callbacks del subsistema de AR.
- `Assets/Editor/ARSceneAutomator.cs`: Módulo de utilidad para el ensamblado procedural de la jerarquía de la escena (`XR Origin`, `ARSession`, `Canvas`, jerarquías y dependencias serializadas).
