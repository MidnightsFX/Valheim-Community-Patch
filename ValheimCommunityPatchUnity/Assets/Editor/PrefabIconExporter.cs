using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
///     Unity Editor Tool: Prefab Icon Exporter
///     Renders a prefab using a custom camera and exports it as a PNG with a transparent background.
///     Place this file anywhere inside an Editor folder in your project.
///     Open via: Tools > Prefab Icon Exporter
/// </summary>
public class PrefabIconExporter : EditorWindow
{
    private const int HiddenLayer = 31;

    private GameObject _prefab;
    private GameObject prefab
    {
        get => _prefab;
        set
        {
            _prefab = value;
            filename = value != null ? value.name + ".png" : "";
        }
    }

    private readonly List<GameObject> batchPrefabs = new();
    private Vector2 batchScroll;
    private bool showBatch;

    private bool isBatchRunning;
    private int batchTotal;
    private int batchDone;
    private string batchStatus = "";

    private int width = 256;
    private int height = 256;

    private static readonly int[] PresetSizes = { 64, 128, 256, 512, 1024 };
    private static readonly string[] PresetLabels = { "64", "128", "256", "512", "1024" };

    private float cameraDistance = 1f;
    private float cameraElevation = -15f;
    private float cameraRotationY = -145f;
    private float cameraVerticalOffset;
    private float objectRotationY;
    private float fieldOfView = 30f;

    private Color lightColor = Color.white;
    private float lightIntensity = 1.2f;
    private float lightRotationY = 120f;
    private float lightElevation = 50f;
    private bool addFillLight = true;
    private Color fillLightColor = new(0.4f, 0.45f, 0.55f);
    private float fillIntensity = 0.4f;

    private int antiAliasingSamples = 4;
    private static readonly int[] AaSamples = { 1, 2, 4, 8 };
    private static readonly string[] AaLabels = { "None (1×)", "2×", "4×", "8×" };

    private string outputFolder = "Assets/PrefabIcons";
    private string filename = "";
    private bool openAfterExport = true;

    private Vector2 scroll;
    private Texture2D previewTexture;
    private bool previewDirty = true;

    private bool showDimensions = true;
    private bool showCamera = true;
    private bool showLighting = true;
    private bool showQuality = true;
    private bool showOutput = true;

    [MenuItem("Tools/Prefab Icon Exporter")]
    public static void OpenWindow()
    {
        PrefabIconExporter win = GetWindow<PrefabIconExporter>("Prefab Icon Exporter");
        win.minSize = new Vector2(360, 680);
        win.Show();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawPrefabField();
        EditorGUILayout.Space(4);

        DrawSection("Image Dimensions", ref showDimensions, DrawDimensions);
        DrawSection("Camera Settings", ref showCamera, DrawCamera);
        DrawSection("Lighting", ref showLighting, DrawLighting);
        DrawSection("Render Quality", ref showQuality, DrawQuality);
        DrawSection("Output", ref showOutput, DrawOutput);
        DrawSection("Batch Export", ref showBatch, DrawBatch);

        EditorGUILayout.Space(8);
        DrawPreviewAndExport();

        EditorGUILayout.EndScrollView();
    }

    private void DrawPrefabField()
    {
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.Space(6);

        GameObject newPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Prefab", "The prefab or scene object to render."),
            prefab, typeof(GameObject), true);

        if (EditorGUI.EndChangeCheck())
        {
            prefab = newPrefab;
            previewDirty = true;
        }
    }

    private void DrawDimensions()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Preset", GUILayout.Width(EditorGUIUtility.labelWidth));

        for (int i = 0; i < PresetSizes.Length; ++i)
        {
            if (GUILayout.Button(PresetLabels[i], EditorStyles.miniButton))
            {
                width = PresetSizes[i];
                height = PresetSizes[i];
                previewDirty = true;
            }
        }

        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        width = Mathf.Max(16, EditorGUILayout.IntField(new GUIContent("Width (px)"), width));
        height = Mathf.Max(16, EditorGUILayout.IntField(new GUIContent("Height (px)"), height));

        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
        }
    }

    private void DrawCamera()
    {
        EditorGUI.BeginChangeCheck();

        cameraDistance =
            EditorGUILayout.Slider(new GUIContent("Distance", "How far the camera sits from the object's pivot."),
                cameraDistance, 0.1f, 20f);
        cameraRotationY =
            EditorGUILayout.Slider(new GUIContent("Horizontal", "Orbit angle around the Y axis (degrees)."),
                cameraRotationY, -180f, 180f);
        cameraElevation =
            EditorGUILayout.Slider(
                new GUIContent("Elevation", "Angle above (positive) or below (negative) the horizon (degrees)."),
                cameraElevation, -89f, 89f);
        cameraVerticalOffset =
            EditorGUILayout.Slider(
                new GUIContent("Vertical Offset",
                    "Slides the object up (positive) or down (negative) within the frame, relative to its size."),
                cameraVerticalOffset, -1f, 1f);
        objectRotationY =
            EditorGUILayout.Slider(
                new GUIContent("Object Spin (Y)", "Rotates the object about Unity's Y axis (degrees)."),
                objectRotationY, -180f, 180f);
        fieldOfView = EditorGUILayout.Slider(new GUIContent("Field of View"), fieldOfView, 5f, 120f);

        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
        }
        
        if (GUILayout.Button("Reset Camera", EditorStyles.miniButton))
        {
            cameraDistance = 1f;
            cameraElevation = -15f;
            cameraRotationY = -120f;
            cameraVerticalOffset = 0f;
            objectRotationY = 0f;
            fieldOfView = 30f;
            previewDirty = true;
        }
    }

    private void DrawLighting()
    {
        EditorGUI.BeginChangeCheck();

        lightColor = EditorGUILayout.ColorField(new GUIContent("Key Light Color"), lightColor);
        lightIntensity = EditorGUILayout.Slider(new GUIContent("Key Intensity"), lightIntensity, 0f, 4f);
        lightRotationY = EditorGUILayout.Slider(new GUIContent("Key Horizontal"), lightRotationY, -180f, 180f);
        lightElevation = EditorGUILayout.Slider(new GUIContent("Key Elevation"), lightElevation, 0f, 89f);
        addFillLight = EditorGUILayout.Toggle(new GUIContent("Fill Light"), addFillLight);

        if (addFillLight)
        {
            EditorGUI.indentLevel++;
            fillLightColor = EditorGUILayout.ColorField(new GUIContent("Fill Color"), fillLightColor);
            fillIntensity = EditorGUILayout.Slider(new GUIContent("Fill Intensity"), fillIntensity, 0f, 2f);
            EditorGUI.indentLevel--;
        }

        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
        }

        if (GUILayout.Button("Reset Lighting", EditorStyles.miniButton))
        {
            lightColor = Color.white;
            lightIntensity = 1.2f;
            lightRotationY = 135f;
            lightElevation = 50f;
            addFillLight = true;
            fillLightColor = new Color(0.4f, 0.45f, 0.55f);
            fillIntensity = 0.4f;
            previewDirty = true;
        }
    }

    private void DrawQuality()
    {
        EditorGUI.BeginChangeCheck();

        int currentIndex = Array.IndexOf(AaSamples, antiAliasingSamples);
        if (currentIndex < 0)
        {
            currentIndex = 2;
        }

        int newIndex = EditorGUILayout.Popup(
            new GUIContent("Anti-Aliasing (MSAA)", "Higher values = smoother edges, slower render."),
            currentIndex, AaLabels);
        antiAliasingSamples = AaSamples[newIndex];

        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
        }
    }

    private void DrawOutput()
    {
        EditorGUILayout.BeginHorizontal();
        outputFolder = EditorGUILayout.TextField(new GUIContent("Output Folder"), outputFolder);

        if (GUILayout.Button("…", GUILayout.Width(24)))
        {
            string chosen = EditorUtility.OpenFolderPanel("Select Output Folder", outputFolder, "");
            if (!string.IsNullOrEmpty(chosen))
            {
                if (chosen.StartsWith(Application.dataPath))
                {
                    chosen = "Assets" + chosen.Substring(Application.dataPath.Length);
                }
                outputFolder = chosen;
            }
        }

        EditorGUILayout.EndHorizontal();

        filename = EditorGUILayout.TextField(new GUIContent("File Name", "Leave blank to use the prefab's name."),
            filename);
        openAfterExport = EditorGUILayout.Toggle(new GUIContent("Ping after export"), openAfterExport);
    }

    private void DrawBatch()
    {
        EditorGUILayout.HelpBox(
            "All render settings above apply to every prefab in the queue.\n" +
            "Each icon is saved as <prefab name>.png in the Output Folder.",
            MessageType.Info);

        EditorGUILayout.Space(2);

        batchScroll = EditorGUILayout.BeginScrollView(batchScroll, GUILayout.MaxHeight(160));

        for (var i = 0; i < batchPrefabs.Count; ++i)
        {
            EditorGUILayout.BeginHorizontal();

            batchPrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                batchPrefabs[i], typeof(GameObject), true);

            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
            {
                batchPrefabs.RemoveAt(i);
                --i;
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("+ Add Slot", EditorStyles.miniButton))
            batchPrefabs.Add(null);

        if (GUILayout.Button("Add Selected", EditorStyles.miniButton))
        {
            for (int i = 0; i < Selection.objects.Length; ++i)
            {
                Object obj = Selection.objects[i];
                if (obj is GameObject go && !batchPrefabs.Contains(go))
                    batchPrefabs.Add(go);
            }
        }

        EditorGUI.BeginDisabledGroup(batchPrefabs.Count == 0);
        if (GUILayout.Button("Clear All", EditorStyles.miniButton))
        {
            batchPrefabs.Clear();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        if (isBatchRunning)
        {
            float progress = batchTotal > 0 ? (float)batchDone / batchTotal : 0f;
            Rect barRect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(barRect, progress, batchStatus);
            EditorGUILayout.Space(2);
        }

        int validCount = CountValidBatchEntries();
        bool canBatch = validCount > 0 && !isBatchRunning;

        EditorGUI.BeginDisabledGroup(!canBatch);

        GUIStyle batchStyle = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12
        };

        if (GUILayout.Button($"Export Batch ({validCount} prefab{(validCount == 1 ? "" : "s")})", batchStyle,
                GUILayout.Height(32)))
        {
            RunBatchExport();
        }

        EditorGUI.EndDisabledGroup();
    }

    private void DrawPreviewAndExport()
    {
        bool canRender = prefab != null;

        EditorGUI.BeginDisabledGroup(!canRender);
        if (GUILayout.Button("Refresh Preview", GUILayout.Height(28)))
        {
            previewDirty = true;
        }
        EditorGUI.EndDisabledGroup();

        if (canRender && previewDirty)
        {
            DestroyImmediate(previewTexture);
            previewTexture = RenderIcon(prefab, Mathf.Min(width, 256), Mathf.Min(height, 256));
            previewDirty = false;
        }

        if (previewTexture != null)
        {
            float aspect = (float)previewTexture.width / previewTexture.height;
            float previewW = Mathf.Min(position.width - 32, 256);
            float previewH = previewW / aspect;

            Rect checkRect = GUILayoutUtility.GetRect(previewW, previewH, GUILayout.ExpandWidth(false));
            checkRect.x = (position.width - previewW) * 0.5f;
            DrawCheckerboard(checkRect);
            GUI.DrawTexture(checkRect, previewTexture, ScaleMode.ScaleToFit, true);
            EditorGUILayout.Space(4);
        }

        EditorGUILayout.Space(4);

        EditorGUI.BeginDisabledGroup(!canRender);

        GUIStyle exportStyle = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 13
        };

        if (GUILayout.Button("Export PNG", exportStyle, GUILayout.Height(36)))
        {
            ExportIcon();
        }

        EditorGUI.EndDisabledGroup();

        if (!canRender)
        {
            EditorGUILayout.HelpBox("Assign a prefab or scene GameObject above to enable export.", MessageType.Info);
        }

        EditorGUILayout.Space(8);
    }

    private int CountValidBatchEntries()
    {
        int count = 0;
        for (int i = 0; i < batchPrefabs.Count; ++i)
        {
            GameObject go = batchPrefabs[i];
            if (go != null)
            {
                ++count;
            }
        }

        return count;
    }

    private void RunBatchExport()
    {
        List<GameObject> queue = new List<GameObject>();
        for (int i = 0; i < batchPrefabs.Count; ++i)
        {
            GameObject go = batchPrefabs[i];
            if (go != null)
            {
                queue.Add(go);
            }
        }

        if (queue.Count == 0) return;

        isBatchRunning = true;
        batchTotal = queue.Count;
        batchDone = 0;
        batchStatus = "";

        string absFolder = outputFolder.StartsWith("Assets")
            ? Path.Combine(Application.dataPath, "..", outputFolder)
            : outputFolder;
        absFolder = Path.GetFullPath(absFolder);
        Directory.CreateDirectory(absFolder);

        int succeeded = 0;
        int failed = 0;
        string absPath = "";

        for (int i = 0; i < queue.Count; ++i)
        {
            GameObject go = queue[i];
            batchStatus = $"Rendering {go.name}… ({i + 1}/{batchTotal})";
            Repaint();

            try
            {
                Texture2D tex = RenderIcon(go, width, height);

                string baseName = go.name + ".png";
                absPath = Path.Combine(absFolder, baseName);
                File.WriteAllBytes(absPath, tex.EncodeToPNG());
                DestroyImmediate(tex);

                Debug.Log($"PrefabIconExporter [batch]: saved {width}×{height} → {absPath}");
                ++succeeded;
            }
            catch (Exception ex)
            {
                Debug.LogError($"PrefabIconExporter [batch]: failed on '{go.name}' — {ex.Message}");
                ++failed;
            }

            batchDone = i + 1;
        }

        AssetDatabase.Refresh();

        isBatchRunning = false;
        batchStatus = $"Done — {succeeded} exported, {failed} failed.";

        Debug.Log($"PrefabIconExporter: batch complete. {succeeded}/{batchTotal} exported to {absFolder}");
        Repaint();

        if (openAfterExport)
        {
            string relPath = absPath.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (relPath.StartsWith(dataPath))
            {
                relPath = "Assets" + relPath.Substring(dataPath.Length);
            }

            Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
            if (asset != null)
                EditorGUIUtility.PingObject(asset);
        }
    }
    
    private Texture2D RenderIcon(GameObject sourcePrefab, int texWidth, int texHeight)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
        instance.hideFlags = HideFlags.HideAndDontSave;
        SetLayerRecursively(instance, HiddenLayer);
        instance.transform.position = Vector3.zero;
        instance.transform.rotation = Quaternion.Euler(0f, objectRotationY, 0f);
        instance.transform.localScale = Vector3.one;

        Bounds bounds = ComputeBounds(instance);
        float radius = bounds.extents.magnitude;
        if (radius < 0.0001f) radius = 1f;

        float autoDistance = radius / Mathf.Sin(Mathf.Deg2Rad * fieldOfView * 0.5f) * cameraDistance;
        Quaternion camRot = Quaternion.Euler(-cameraElevation, cameraRotationY, 0f);

        // Slide the whole view down by the offset so the object appears to move up in the
        // frame (and vice versa) without altering the viewing angle. Scaled by the object's size.
        Vector3 framingShift = Vector3.down * (cameraVerticalOffset * radius);
        Vector3 lookTarget = bounds.center + framingShift;
        Vector3 camPos = lookTarget + camRot * (Vector3.forward * -autoDistance);

        GameObject camGO = new GameObject("__IconExportCam") { hideFlags = HideFlags.HideAndDontSave };
        Camera cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << HiddenLayer;
        cam.fieldOfView = fieldOfView;
        cam.nearClipPlane = autoDistance * 0.01f;
        cam.farClipPlane = autoDistance * 10f;
        cam.transform.position = camPos;
        cam.transform.LookAt(lookTarget);
        cam.allowMSAA = antiAliasingSamples > 1;
        cam.forceIntoRenderTexture = true;

        GameObject keyLightGO = CreateDirectionalLight("__KeyLight", lightColor, lightIntensity, lightRotationY, lightElevation, HiddenLayer);
        GameObject fillLightGO = null;
        if (addFillLight)
        {
            fillLightGO = CreateDirectionalLight("__FillLight", fillLightColor, fillIntensity, lightRotationY + 180f, -lightElevation * 0.5f, HiddenLayer);
        }

        RenderTextureDescriptor rtDesc = new RenderTextureDescriptor(texWidth, texHeight, RenderTextureFormat.ARGB32, 24)
        {
            msaaSamples = antiAliasingSamples
        };

        RenderTexture rt = new RenderTexture(rtDesc);
        RenderTexture rtResolved = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.ARGB32);

        cam.targetTexture = rt;
        cam.Render();
        Graphics.Blit(rt, rtResolved);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rtResolved;
        Texture2D result = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false, false);
        result.ReadPixels(new Rect(0, 0, texWidth, texHeight), 0, 0);
        result.Apply();

        RenderTexture.active = previous;

        DestroyImmediate(camGO);
        DestroyImmediate(keyLightGO);
        if (fillLightGO != null)
        {
            DestroyImmediate(fillLightGO);
        }
        DestroyImmediate(instance);
        rt.Release();
        DestroyImmediate(rt);
        rtResolved.Release();
        DestroyImmediate(rtResolved);

        return result;
    }

    private void ExportIcon()
    {
        Texture2D tex = RenderIcon(prefab, width, height);
        if (tex == null)
        {
            Debug.LogError("PrefabIconExporter: render returned null.");
            return;
        }

        string baseName = string.IsNullOrWhiteSpace(filename) ? prefab.name : filename.Trim();
        if (!baseName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            baseName += ".png";
        }

        string absFolder = outputFolder.StartsWith("Assets")
            ? Path.Combine(Application.dataPath, "..", outputFolder)
            : outputFolder;
        absFolder = Path.GetFullPath(absFolder);
        Directory.CreateDirectory(absFolder);

        string absPath = Path.Combine(absFolder, baseName);
        File.WriteAllBytes(absPath, tex.EncodeToPNG());
        DestroyImmediate(tex);

        Debug.Log($"PrefabIconExporter: saved {width}×{height} icon → {absPath}");

        AssetDatabase.Refresh();

        if (openAfterExport)
        {
            string relPath = absPath.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (relPath.StartsWith(dataPath))
                relPath = "Assets" + relPath.Substring(dataPath.Length);

            Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
            }
        }
    }
    
    private static GameObject CreateDirectionalLight(string lightName, Color color, float intensity, float rotY,
        float elevation, int layer)
    {
        GameObject go = new GameObject(lightName) { hideFlags = HideFlags.HideAndDontSave };
        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = color;
        light.intensity = intensity;
        light.cullingMask = 1 << layer;
        go.transform.rotation = Quaternion.Euler(-elevation, rotY, 0f);
        return go;
    }

    private static Bounds ComputeBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return new Bounds(go.transform.position, Vector3.one);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; ++i)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    private static void DrawHorizontalLine(float thickness = 1f)
    {
        Rect rect = EditorGUILayout.GetControlRect(false, thickness);
        rect.height = thickness;
        EditorGUI.DrawRect(rect, new Color(0.35f, 0.35f, 0.35f));
    }

    private static void DrawSection(string title, ref bool foldout, Action drawContent)
    {
        EditorGUILayout.Space(4);
        foldout = EditorGUILayout.BeginFoldoutHeaderGroup(foldout, title);

        if (foldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(2);
            drawContent();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }

        EditorGUILayout.EndFoldoutHeaderGroup();
        DrawHorizontalLine();
    }

    private static Texture2D _checkerTex;

    private static void DrawCheckerboard(Rect rect)
    {
        if (_checkerTex == null)
        {
            _checkerTex = new Texture2D(2, 2) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            Color c0 = new Color(0.6f, 0.6f, 0.6f);
            Color c1 = new Color(0.9f, 0.9f, 0.9f);
            _checkerTex.SetPixels(new[] { c0, c1, c1, c0 });
            _checkerTex.Apply();
        }

        Matrix4x4 saved = GUI.matrix;
        GUI.DrawTextureWithTexCoords(rect, _checkerTex, new Rect(0, 0, rect.width / 16f, rect.height / 16f));
        GUI.matrix = saved;
    }
}