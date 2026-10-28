using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AnimationEventEditor
{
    /// <summary>
    /// Editor window for setting up animation events on Model importer clips.
    /// Events are stored in ModelImporterClipAnimation.events and persist on reimport.
    /// </summary>
    public class AnimationEventEditor : EditorWindow
    {
        // --- Layout --------------------------------------------------------------
        private const float MinSplit = 200f;
        private const float MinRightWidth = 200f;
        private float split = 300f;
        private bool isResizing;

        // Favorites panel
        private bool isFavResizing;
        private int selectedFavIndex = -1;
        private Vector2 favScroll;
        private const float MinFavWidth = 80f;

        // Prefs-backed properties
        private bool ShowFavorites
        {
            get => AnimationEventEditorPrefs.instance.showFavorites;
            set { AnimationEventEditorPrefs.instance.showFavorites = value; AnimationEventEditorPrefs.instance.SavePrefs(); }
        }
        private float FavPanelWidth
        {
            get => AnimationEventEditorPrefs.instance.favoritesPanelWidth;
            set => AnimationEventEditorPrefs.instance.favoritesPanelWidth = value; // saved on MouseUp
        }
        private List<GameObject> FavoriteModels => AnimationEventProjectPrefs.instance.favoriteModels;

        // --- Model -----------------------------------------------------------------
        private GameObject selectedModel;
        private string modelPath;
        private ModelImporter modelImporter;
        private bool isDirty;

        // --- Clips ---------------------------------------------------------------
        private ModelImporterClipAnimation[] workingClips;
        private string[] clipNames;
        private int selectedClipIndex;

        //--- Scrubber -------------------------------------------------------------
        private float currentTimeScrubberTime;
        private bool isScrubberDragging;
        
        // --- SessionState keys (survive domain reloads within the same Unity session) --
        private const string SessionModelPath   = "AnimEventEditor.modelPath";
        private const string SessionClipIndex = "AnimEventEditor.clipIndex";

        // --- Working events for the selected clip --------------------------------
        private List<AnimationEvent> workingEvents = new();

        private int _selectedEventIndex = -1;
        private int selectedEventIndex
        {
            get => _selectedEventIndex;
            set
            {
                _selectedEventIndex = value; 
                InitPreviewScene();
            }
        }

        // --- Clip info ------------------------------------------------------------
        private AnimationClip previewClip;
        private float clipLength;
        private float frameRate;
        private int frameCount;

        // --- Timeline ------------------------------------------------------------
        private Vector2 timelineScroll;
        private float zoom = 1f;
        private const float BaseFrameWidth = 14f;
        private const float MinZoom = 0.1f;
        private const float MaxZoom = 16f;
        private float FrameWidth => BaseFrameWidth * zoom;
        private const float TimelineBorder = 30f;
        private const float TickRowHeight = 22f;
        private const float FunctionRowHeight = 36f;   // tall enough for two sub-rows
        private static readonly int[] NiceTickSteps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };

        private float timelineViewWidth;
        private float timelineContentW;
        private bool isPanning;
        private float lastDragViewX;
        private bool lastDragSnap;

        // Event dragging
        private bool isDraggingEvent;
        private int dragEventIndex = -1;

        // ---- Preview Panel ------------------------------------------------------
        private bool isPreviewSceneInited;
        private GameObject previewSceneObject;
        private PreviewRenderUtility _previewUtility;

        private const string PrefPreviewVisible = "FbxAnimEventEditor.previewVisible";
        private const string PrefPreviewSize = "FbxAnimEventEditor.previewSize";
        private const float PreviewFov = 30f;
        private const float DefaultPreviewPitch = 20f;
        private const float MinPreviewSize = 170f;
        private const float PreviewMargin = 12f;
        private const float PreviewGripSize = 14f;
        private const float TimelineHintH = 16f;

        private bool previewVisible = true;
        private float previewSize = 240f;
        private float previewYaw;
        private float previewPitch = DefaultPreviewPitch;
        private float previewZoom = 1f;
        private float previewRadius = 1f;
        private Vector3 previewCenter = Vector3.up;
        private bool previewPlaying;
        private bool previewLoop = true;
        private double lastUpdateTime;
        
        // --- Reference object / method discovery ---------------------------------
        private GameObject referenceObject;

        private struct DiscoveredMethod
        {
            public string MethodName;
            public Type   ParamType;   // null = no parameter
            public string MenuPath;    // "ClassName/MethodName(paramType)"
        }

        private readonly List<DiscoveredMethod> discoveredMethods = new();

        private static readonly HashSet<string> ExcludedMethodNames = new()
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy",
            "CancelInvoke", "StopCoroutine", "StopAllCoroutines",
            "SendMessage", "SendMessageUpwards", "BroadcastMessage",
        };

        // --- Colors ---------------------------------------------------------------
        private static readonly Color ColBackground    = new(0.18f, 0.18f, 0.18f);
        private static readonly Color ColTickLine      = new(0.55f, 0.55f, 0.55f, 0.8f);
        private static readonly Color ColTickMinor     = new(0.35f, 0.35f, 0.35f, 0.5f);
        private static readonly Color ColEventRow      = new(0.22f, 0.22f, 0.22f);
        private static readonly Color ColEventNormal   = new(0.85f, 0.55f, 0.1f);
        private static readonly Color ColEventSelected = new(1f, 0.85f, 0.2f);
        private static readonly Color ColEventNoFunc   = new(0.8f, 0.25f, 0.25f);
        private static readonly Color ColSplitter      = new(0f, 0f, 0f, 0.5f);
        private static readonly Color ColSeparator     = new(0f, 0f, 0f, 0.3f);
        private static readonly Color ColTimeScrubberNormal   = new(0.1f, 0.55f, 0.85f);
        private static readonly Color ColTimeScrubberHovered  = new(0.2f, 0.65f, 0.95f);
        private static readonly Color ColTimeScrubberDragging  = new(0.3f, 0.75f, 1f);

        // --- GUIStyles -------------------------------------------------------------
        private GUIStyle dropDownWithSmallText = null;
        private GUIStyle previewHintStyle = null;
        
        // --- Menu -----------------------------------------------------------------
        [MenuItem("Window/Animation/Animation Events")]
        public static void ShowWindow()
        {
            var window = GetWindow<AnimationEventEditor>("Animation Events");
            window.titleContent.image = EditorGUIUtility.IconContent("AnimationClip On Icon").image;
            window.minSize = new Vector2(520, 320);
        }

        // --- Lifecycle ------------------------------------------------------------
        private void OnEnable()
        {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += OnEditorUpdate;
            lastUpdateTime = EditorApplication.timeSinceStartup;
            dropDownWithSmallText = null;
            previewHintStyle = null;
            previewVisible = EditorPrefs.GetBool(PrefPreviewVisible, true);
            previewSize = EditorPrefs.GetFloat(PrefPreviewSize, 240f);
            
            RefreshClipInfo();
            InitPreviewScene();
            
            // Restore persisted reference object.
            referenceObject = AnimationEventProjectPrefs.instance.referenceAnimatorOwner;

            // Re-acquire importer after domain reload (modelImporter is not serialized
            // but modelPath survives via [SF]).
            if (!string.IsNullOrEmpty(modelPath) && modelImporter == null)
            {
                modelImporter = AssetImporter.GetAtPath(modelPath) as ModelImporter;
                if (modelImporter != null)
                    LoadFromImporter(); // re-populate workingClips from disk
            }

            OnSelectionChanged();
            RefreshMethods();
        }

        private void InitGuiStyles()
        {
            dropDownWithSmallText = dropDownWithSmallText ?? new GUIStyle(EditorStyles.toolbarDropDown)
            {
                fontSize = EditorStyles.miniFont.fontSize - 1,
                contentOffset = new Vector2(0, 1)
            };
            dropDownWithSmallText.normal = EditorStyles.toolbarDropDown.normal;
            dropDownWithSmallText.hover = EditorStyles.toolbarDropDown.hover;
            dropDownWithSmallText.active = EditorStyles.toolbarDropDown.active;
            dropDownWithSmallText.focused = EditorStyles.toolbarDropDown.focused;
            dropDownWithSmallText.onHover = EditorStyles.toolbarDropDown.onHover;

            previewHintStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(1f, 1f, 1f, 0.45f) },
            };
        }

        private void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float deltaTime = (float)(now - lastUpdateTime);
            lastUpdateTime = now;

            if (!previewPlaying || clipLength <= 0f) return;

            float time = currentTimeScrubberTime + deltaTime;
            if (time >= clipLength)
            {
                if (previewLoop) time %= clipLength;
                else
                {
                    time = clipLength;
                    previewPlaying = false;
                }
            }
            currentTimeScrubberTime = time;
            if (!isScrubberDragging && !isDraggingEvent) EnsureTimeVisible(time);
            Repaint();
        }
        
        private void InitPreviewScene()
        {
            if (isPreviewSceneInited)
            {
                isPreviewSceneInited = false;
                // clean up old scene?
                _previewUtility.Cleanup();
                _previewUtility = null;
            }

            var model = referenceObject != null ?
                referenceObject.GetComponentInChildren<Animator>() is {} animator ? animator.gameObject : 
                referenceObject : selectedModel;
            
            if (selectedModel != null && model != null)
            {
                _previewUtility = new PreviewRenderUtility(true);
                _previewUtility.camera.fieldOfView = PreviewFov;
                _previewUtility.camera.nearClipPlane = 0.01f;
                _previewUtility.camera.farClipPlane = 1000f;

                // 3. Set up default lighting (Matches Unity's Inspector style)
                _previewUtility.lights[0].intensity = 1.4f;
                _previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                _previewUtility.lights[1].intensity = 0.4f;
                
                previewSceneObject = _previewUtility.InstantiatePrefabInScene(model);
                if (previewSceneObject == null)
                {
                    isPreviewSceneInited = true;
                    return;
                }
                previewSceneObject.transform.position = Vector3.zero;
                previewSceneObject.transform.rotation = Quaternion.LookRotation(-Vector3.forward, Vector3.up);
                previewSceneObject.hideFlags = HideFlags.HideAndDontSave;
                
                if(previewClip != null) previewClip.SampleAnimation(previewSceneObject, currentTimeScrubberTime);
                FramePreviewCamera();
                ResetPreviewCamera();
                isPreviewSceneInited = true;
            }
        }

        private void FramePreviewCamera()
        {
            Bounds bounds = default;
            bool hasBounds = false;
            foreach (var renderer in previewSceneObject.GetComponentsInChildren<Renderer>())
            {
                if (renderer is not (SkinnedMeshRenderer or MeshRenderer)) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds) bounds = new Bounds(Vector3.up, Vector3.one * 2f);

            previewCenter = bounds.center;
            previewRadius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        }

        private void ResetPreviewCamera()
        {
            previewYaw = 0f;
            previewPitch = DefaultPreviewPitch;
            previewZoom = 1f;
            Repaint();
        }

        private void UpdatePreviewCamera(float aspect)
        {
            float halfVerticalFov = PreviewFov * 0.5f * Mathf.Deg2Rad;
            float halfHorizontalFov = Mathf.Atan(Mathf.Tan(halfVerticalFov) * aspect);
            float framedDistance = previewRadius * 1.1f / Mathf.Sin(Mathf.Min(halfVerticalFov, halfHorizontalFov));

            Quaternion rotation = Quaternion.Euler(previewPitch, previewYaw, 0f);
            Transform cameraTransform = _previewUtility.camera.transform;
            cameraTransform.rotation = rotation;
            cameraTransform.position = previewCenter - rotation * Vector3.forward * (framedDistance * previewZoom);
        }

        private GameObject testTarget;

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.update -= OnEditorUpdate;
            // Persist panel widths that we only save on-change-end
            AnimationEventEditorPrefs.instance?.SavePrefs();
            AnimationEventProjectPrefs.instance?.SavePrefs();

            dropDownWithSmallText = null;
            
            if (_previewUtility != null)
            {
                _previewUtility.Cleanup();
                _previewUtility = null;
                isPreviewSceneInited = false;
            }
        }

        private void OnSelectionChanged()
        {
            var active = Selection.activeObject;
            if (active == null) return;

            string path = AssetDatabase.GetAssetPath(active);
            // TODO: validate selection
            if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                // The root asset of an FBX is its imported GameObject.
                var modelRoot = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                if (modelRoot != null)
                {
                    TrySetModel(modelRoot);
                    Repaint();
                }
            }

        }

        // --- Method discovery -----------------------------------------------------
        private void RefreshMethods()
        {
            discoveredMethods.Clear();

            if (referenceObject == null)
            {
                Repaint();
                return;
            }

            var refAnimator = referenceObject.GetComponentInChildren<Animator>(true);
            if(refAnimator == null)  return;
            
            // Scan all MonoBehaviours on the object and its children.
            foreach (var comp in refAnimator.GetComponents<MonoBehaviour>())
            {
                if (comp == null) continue;
                string className = comp.GetType().Name;

                foreach (var method in comp.GetType()
                             .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(IsValidAnimationEventMethod))
                {
                    // Deduplicate by method name — last writer wins (same as KMethodHandler).
                    int existing = discoveredMethods.FindIndex(m => m.MethodName == method.Name);

                    var param     = method.GetParameters().FirstOrDefault();
                    string suffix = param != null ? $"({param.ParameterType.Name})" : "()";
                    string path   = $"{className}/{method.Name}{suffix}";

                    var entry = new DiscoveredMethod
                    {
                        MethodName = method.Name,
                        ParamType  = param?.ParameterType,
                        MenuPath   = path,
                    };

                    if (existing >= 0) discoveredMethods[existing] = entry;
                    else               discoveredMethods.Add(entry);
                }
            }

            Repaint();
        }

        private static bool IsValidAnimationEventMethod(MethodInfo m)
        {
            if (m.ReturnType   != typeof(void))  return false;
            if (m.IsSpecialName)                  return false;
            if (ExcludedMethodNames.Contains(m.Name)) return false;

            var parameters = m.GetParameters();
            if (parameters.Length > 1) return false;
            if (parameters.Length == 1 && !IsValidParamType(parameters[0].ParameterType)) return false;
            return true;
        }

        private static bool IsValidParamType(Type t) =>
            t == typeof(string) || t == typeof(int) || t == typeof(float) ||
            t == typeof(AnimationEvent) || t.IsEnum ||
            typeof(Object).IsAssignableFrom(t);

        // --- Model loading ----------------------------------------------------------
        private void TrySetModel(GameObject model)
        {
            if (selectedModel == model) return;

            if (isDirty && selectedModel != null)
            {
                if (!EditorUtility.DisplayDialog("Unsaved Changes",
                        "You have unsaved changes. Discard them?", "Discard", "Cancel"))
                    return;
            }

            selectedModel       = model;
            modelPath           = model != null ? AssetDatabase.GetAssetPath(model) : null;
            modelImporter     = modelPath != null ? AssetImporter.GetAtPath(modelPath) as ModelImporter : null;
            selectedClipIndex = 0;
            isDirty           = false;

            InitPreviewScene();
            
            SessionState.SetString(SessionModelPath, modelPath ?? "");
            SessionState.SetInt(SessionClipIndex, 0);

            LoadFromImporter();
        }

        private void LoadFromImporter()
        {
            workingEvents.Clear();
            selectedEventIndex = -1;
            previewClip        = null;

            if (modelImporter == null)
            {
                workingClips = null;
                clipNames    = null;
                return;
            }

            // Use user-defined clips; fall back to default (Model take) clips if none exist.
            ModelImporterClipAnimation[] source = modelImporter.clipAnimations;
            if (source == null || source.Length == 0)
                source = modelImporter.defaultClipAnimations;

            workingClips = source?.Select(CloneClipAnimation).ToArray()
                           ?? Array.Empty<ModelImporterClipAnimation>();
            clipNames    = workingClips.Select(c => c.name).ToArray();

            if (workingClips.Length > 0)
            {
                // Restore the previously selected clip (e.g. after reimport or domain reload).
                int savedIndex = SessionState.GetInt(SessionClipIndex, 0);
                SwitchToClip(Mathf.Clamp(savedIndex, 0, workingClips.Length - 1));
            }
        }

        private void SwitchToClip(int index)
        {
            if (workingClips == null || index < 0 || index >= workingClips.Length) return;

            selectedClipIndex  = index;
            SessionState.SetInt(SessionClipIndex, index);   // persist for reimport / domain reload

            workingEvents      = workingClips[index].events?.Select(CloneAnimationEvent).ToList()
                                 ?? new List<AnimationEvent>();
            selectedEventIndex = -1;

            RefreshClipInfo();
        }

        private void RefreshClipInfo()
        {
            if (modelImporter == null || workingClips == null || workingClips.Length == 0) return;

            var clip = workingClips[selectedClipIndex];

            // Try to find the actual imported AnimationClip asset for accurate metadata.
            previewClip = AssetDatabase.LoadAllAssetsAtPath(modelPath)
                              .OfType<AnimationClip>()
                              .FirstOrDefault(c => c.name == clip.name
                                                   || c.name == clip.takeName);

            if (previewClip != null)
            {
                clipLength = previewClip.length;
                frameRate  = previewClip.frameRate;
            }
            else
            {
                frameRate  = /*clip.frameRate > 0 ? clip.frameRate :*/ 30f;
                clipLength = frameRate > 0 ? (clip.lastFrame - clip.firstFrame) / frameRate : 0f;
            }

            frameCount = Mathf.RoundToInt(frameRate * clipLength);
        }

        // --- Applying -------------------------------------------------------------
        private void ApplyToImporter()
        {
            // Re-acquire the importer in case it became null after a domain reload.
            if (modelImporter == null && !string.IsNullOrEmpty(modelPath))
                modelImporter = AssetImporter.GetAtPath(modelPath) as ModelImporter;

            if (modelImporter == null)
            {
                Debug.LogWarning("Model Animation Events: No importer found. Cannot apply.");
                return;
            }

            if (workingClips == null)
            {
                Debug.LogWarning("Model Animation Events: Working clips are null (domain reload?). Cannot apply.");
                return;
            }

            FlushEventsToWorkingClip();

            // -- Re-read the clip list from the importer RIGHT NOW, then only patch the
            //    events field.  We must NOT use our cloned workingClips for anything other
            //    than the events array: our clone is intentionally incomplete (it omits
            //    maskType, bodyMask, humanoidOverrideMask, curves, etc.) and passing those
            //    missing/defaulted values to ModelImporter::SplitAnimationClips causes the
            //    native RemovedMaskedCurve crash.
            ModelImporterClipAnimation[] liveClips = modelImporter.clipAnimations;
            bool usingDefaults = liveClips == null || liveClips.Length == 0;
            if (usingDefaults)
                liveClips = modelImporter.defaultClipAnimations;

            if (liveClips == null || liveClips.Length == 0)
            {
                Debug.LogWarning("Model Animation Events: Importer returned no clips.");
                return;
            }

            // Patch only the events on each matching clip.
            for (int i = 0; i < liveClips.Length; i++)
            {
                var working = workingClips.FirstOrDefault(w => w.name == liveClips[i].name);
                if (working != null)
                    liveClips[i].events = working.events ?? Array.Empty<AnimationEvent>();
            }

            Undo.RecordObject(modelImporter, "Apply Model Animation Events");
            modelImporter.clipAnimations = liveClips;

            // -- Defer the actual reimport out of the OnGUI / button-click call stack.
            //    Calling AssetDatabase.ImportAsset() synchronously from inside OnGUI can
            //    cause native crashes in ModelImporter when it tries to access curve data
            //    while the rendering pipeline is mid-frame.
            string pathCapture = modelPath;
            EditorApplication.delayCall += () => PerformDelayedImport(pathCapture);
        }

        private void PerformDelayedImport(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                // Re-acquire importer reference after the import (the import can recreate
                // native objects, invalidating the old reference).
                modelImporter = AssetImporter.GetAtPath(path) as ModelImporter;
                isDirty       = false;
                LoadFromImporter();
                Repaint();
            }
            catch (Exception e)
            {
                Debug.LogError($"Model Animation Events: Import failed for \"{path}\".\n{e}");
            }

        }

        private void RevertFromImporter()
        {
            if (isDirty && !EditorUtility.DisplayDialog("Revert Changes",
                    "Discard all unsaved changes?", "Discard", "Cancel"))
                return;

            isDirty = false;
            LoadFromImporter();
        }

        private void FlushEventsToWorkingClip()
        {
            if (workingClips == null || selectedClipIndex < 0
                || selectedClipIndex >= workingClips.Length) return;

            workingEvents.Sort((a, b) => a.time.CompareTo(b.time));
            workingClips[selectedClipIndex].events = workingEvents.Select(CloneAnimationEvent).ToArray();
        }

        // --- Clone helpers --------------------------------------------------------
        private static AnimationEvent CloneAnimationEvent(AnimationEvent src) => new()
        {
            time                   = src.time,
            functionName           = src.functionName,
            stringParameter        = src.stringParameter,
            intParameter           = src.intParameter,
            floatParameter         = src.floatParameter,
            objectReferenceParameter = src.objectReferenceParameter,
            messageOptions         = src.messageOptions,
        };

        private static ModelImporterClipAnimation CloneClipAnimation(ModelImporterClipAnimation src)
        {
            return new ModelImporterClipAnimation
            {
                name                     = src.name,
                takeName                 = src.takeName,
                firstFrame               = src.firstFrame,
                lastFrame                = src.lastFrame,
                wrapMode                 = src.wrapMode,
                loop                     = src.loop,
                loopTime                 = src.loopTime,
                // loopBlend                = src.loopBlend,
                // loopBlendOrientation     = src.loopBlendOrientation,
                // loopBlendPositionY       = src.loopBlendPositionY,
                // loopBlendPositionXZ      = src.loopBlendPositionXZ,
                lockRootRotation         = src.lockRootRotation,
                lockRootHeightY          = src.lockRootHeightY,
                lockRootPositionXZ       = src.lockRootPositionXZ,
                keepOriginalOrientation  = src.keepOriginalOrientation,
                keepOriginalPositionY    = src.keepOriginalPositionY,
                keepOriginalPositionXZ   = src.keepOriginalPositionXZ,
                heightFromFeet           = src.heightFromFeet,
                mirror                   = src.mirror,
                // frameRate                = src.frameRate,
                cycleOffset              = src.cycleOffset,
                hasAdditiveReferencePose = src.hasAdditiveReferencePose,
                additiveReferencePoseFrame = src.additiveReferencePoseFrame,
                events = src.events?.Select(CloneAnimationEvent).ToArray()
                         ?? Array.Empty<AnimationEvent>(),
            };
        }

        // --- OnGUI ----------------------------------------------------------------
        private void OnGUI()
        {
            InitGuiStyles();
            
            DrawToolbar();

            float toolbarH  = EditorStyles.toolbar.fixedHeight;
            float bodyH     = position.height - toolbarH;

            // Favorites panel occupies the leftmost strip when visible.
            float favW   = ShowFavorites ? FavPanelWidth : 0f;
            float favSep = ShowFavorites ? 4f : 0f;
            float leftOff = favW + favSep;          // x-offset where the left panel starts

            Rect favRect      = new(0,          toolbarH, favW,                                    bodyH);
            Rect favSplitRect = new(favW - 2,   toolbarH, 4f,                                      bodyH);
            Rect leftRect     = new(leftOff,    toolbarH, split,                                   bodyH);
            Rect splitterRect = new(leftOff + split - 2, toolbarH, 4f,                             bodyH);
            Rect rightRect    = new(leftOff + split + 2, toolbarH, position.width - leftOff - split - 2, bodyH);

            // Favorites splitter
            if (ShowFavorites)
            {
                HandleFavSplitterResize(favSplitRect);
                EditorGUI.DrawRect(favSplitRect, ColSplitter);
                GUILayout.BeginArea(favRect, EditorStyles.objectFieldThumb);
                DrawFavoritesPanel();
                GUILayout.EndArea();
            }

            // Main left/right splitter
            HandleSplitterResize(splitterRect, leftOff);
            EditorGUI.DrawRect(splitterRect, ColSplitter);

            GUILayout.BeginArea(leftRect, EditorStyles.objectFieldThumb);
            DrawLeftPanel();
            GUILayout.EndArea();

            GUILayout.BeginArea(rightRect);
            DrawRightPanel(rightRect);
            GUILayout.EndArea();
        }

        // --- Toolbar --------------------------------------------------------------
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Favorites toggle
            bool newShowFav = GUILayout.Toggle(ShowFavorites, "☆ Favorites", EditorStyles.toolbarButton, GUILayout.Width(80));
            if (newShowFav != ShowFavorites) ShowFavorites = newShowFav;

            GUILayout.Space(4);
            GUILayout.Label("Model:", EditorStyles.toolbarButton, GUILayout.Width(32));

            // -- Selected Model (method discovery) ------------------------------
            var newModel = (GameObject)EditorGUILayout.ObjectField(
                selectedModel, typeof(GameObject), false, GUILayout.Width(210));

            if (newModel != selectedModel)
            {
                if (newModel != null)
                {
                    string p = AssetDatabase.GetAssetPath(newModel);
                    if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        TrySetModel(newModel);
                    else
                        EditorUtility.DisplayDialog("Invalid File", "Please select an Model file.", "OK");
                }
                else TrySetModel(null);
            }
            
            GUILayout.Space(15);
            
            // -- Reference object (method discovery) ------------------------------
            int methodCount = discoveredMethods.Count;
            GUILayout.Label("Ref:", EditorStyles.toolbarButton, GUILayout.Width(36));
            var newRef = (GameObject)EditorGUILayout.ObjectField(
                referenceObject, typeof(GameObject), true);
            if (newRef != referenceObject)
            {
                referenceObject = newRef;
                isPreviewSceneInited = false;
                InitPreviewScene();
                AnimationEventProjectPrefs.instance.referenceAnimatorOwner = newRef;
                AnimationEventProjectPrefs.instance.SavePrefs();
                RefreshMethods();
            }
            if (referenceObject != null)
            {
                GUI.color = methodCount > 0 ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.4f);
                GUILayout.Label(methodCount > 0 ? $"{methodCount} methods" : "no methods",
                    EditorStyles.miniLabel, GUILayout.Width(74));
                GUI.color = Color.white;
            }

            GUILayout.FlexibleSpace();

            bool canApply = modelImporter != null;
            GUI.enabled = canApply;

            if (isDirty) GUI.color = new Color(1f, 0.8f, 0.3f);
            if (GUILayout.Button(isDirty ? "Apply*" : "Apply", EditorStyles.toolbarButton, GUILayout.Width(60)))
                ApplyToImporter();
            GUI.color = Color.white;

            if (GUILayout.Button("Revert", EditorStyles.toolbarButton, GUILayout.Width(50)))
                RevertFromImporter();

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        // --- Left panel -----------------------------------------------------------
        private void DrawLeftPanel()
        {
            if (modelImporter == null)
            {
                GUILayout.Space(12);
                EditorGUILayout.HelpBox(
                    "Select an Model file in the Project window or via the picker above.",
                    MessageType.Info);
                return;
            }

            if (workingClips == null || workingClips.Length == 0)
            {
                GUILayout.Space(12);
                EditorGUILayout.HelpBox("No animation clips found in this Model.", MessageType.Warning);
                return;
            }

            // -- Clip selector ----------------------------------------------------
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Clip", GUILayout.Width(36));
            int newIdx = EditorGUILayout.Popup(selectedClipIndex, clipNames, EditorStyles.toolbarDropDown);
            EditorGUILayout.EndHorizontal();
            if (newIdx != selectedClipIndex)
            {
                FlushEventsToWorkingClip();
                SwitchToClip(newIdx);
            }

            // -- Clip metadata ----------------------------------------------------
            if (frameCount > 0 || clipLength > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(
                    $"Frames: {frameCount}   FPS: {frameRate:F0}   Length: {clipLength:F3} s",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), ColSeparator);

            // -- Event list header -------------------------------------------------
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"Events ({workingEvents.Count})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(EditorGUIUtility.IconContent("Toolbar Plus"), EditorStyles.toolbarButton,
                    GUILayout.Width(26)))
                AddEvent();

            GUI.enabled = selectedEventIndex >= 0 && selectedEventIndex < workingEvents.Count;
            if (GUILayout.Button(EditorGUIUtility.IconContent("Toolbar Minus"), EditorStyles.toolbarButton,
                    GUILayout.Width(26)))
                RemoveSelectedEvent();
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // -- Event list --------------------------------------------------------
            DrawEventList();

            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), ColSeparator);

            // -- Selected event properties -----------------------------------------
            GUILayout.FlexibleSpace();

            if (selectedEventIndex >= 0 && selectedEventIndex < workingEvents.Count)
                DrawEventProperties(workingEvents[selectedEventIndex]);
            else
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox("Select an event on the timeline to edit it.", MessageType.None);
            }
        }

        private void DrawEventList()
        {
            if (workingEvents.Count == 0)
            {
                GUILayout.Space(4);
                GUILayout.Label("No events. Click + or double-click the timeline.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GUILayout.Space(2);
            for (int i = 0; i < workingEvents.Count; i++)
            {
                var evt      = workingEvents[i];
                bool selected = i == selectedEventIndex;
                bool hasFunc = !string.IsNullOrEmpty(evt.functionName);

                Rect row = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight + 2,
                    GUILayout.ExpandWidth(true));

                EditorGUI.DrawRect(row,
                    selected ? new Color(0.24f, 0.37f, 0.59f) :
                    i % 2 == 0 ? new Color(0.21f, 0.21f, 0.21f) : new Color(0.18f, 0.18f, 0.18f));

                Color dotColor = hasFunc ? ColEventNormal : ColEventNoFunc;
                EditorGUI.DrawRect(new Rect(row.x + 4, row.y + 5, 6, 6), dotColor);

                string label = GetEventStringName(evt, "(no function)");
                string timeLabel = $"{evt.time:F3}s";

                GUI.Label(new Rect(row.x + 14, row.y + 1, row.width - 60, row.height - 2),
                    label, EditorStyles.miniLabel);
                GUI.Label(new Rect(row.xMax - 52, row.y + 1, 50, row.height - 2),
                    timeLabel, EditorStyles.miniLabel);

                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    selectedEventIndex = i;
                    GUI.FocusControl(null);
                    Repaint();
                }
            }

            GUILayout.Space(2);
        }

        private void DrawEventProperties(AnimationEvent evt)
        {
            GUILayout.Label("Event Properties", EditorStyles.boldLabel);
            GUILayout.Space(2);

            EditorGUI.BeginChangeCheck();

            // Time row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Time", GUILayout.Width(72));
            float newTime = EditorGUILayout.FloatField(evt.time);
            newTime = Mathf.Clamp(newTime, 0f, clipLength > 0 ? clipLength : float.MaxValue);
            GUILayout.Label("s", GUILayout.Width(14));
            EditorGUILayout.EndHorizontal();

            // Frame row
            if (frameRate > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Frame", GUILayout.Width(72));
                int curFrame = Mathf.RoundToInt(evt.time * frameRate);
                int newFrame = EditorGUILayout.IntField(curFrame);
                if (newFrame != curFrame)
                    newTime = Mathf.Clamp(newFrame / frameRate, 0f, clipLength > 0 ? clipLength : float.MaxValue);
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(4);

            GUILayout.Space(2);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), ColSeparator);
            GUILayout.Space(2);
            
            // -- Function ----------------------------------------------------------
            // Find the discovered method matching the current function name (if any).
            int matchIdx = discoveredMethods.FindIndex(m => m.MethodName == evt.functionName);
            Type activeParamType = matchIdx >= 0 ? discoveredMethods[matchIdx].ParamType : null;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Function", GUILayout.Width(72));

            string newFunc;

            if (discoveredMethods.Count > 0)
            {
                // Dropdown button showing the currently selected method (or warning).
                bool notFound = !string.IsNullOrEmpty(evt.functionName) && matchIdx < 0;
                string btnLabel = matchIdx >= 0
                    ? discoveredMethods[matchIdx].MenuPath
                    : string.IsNullOrEmpty(evt.functionName) ? "— Select —"
                    : $"⚠ {evt.functionName}";

                if (notFound) GUI.color = new Color(1f, 0.55f, 0.55f);
                Rect dropRect = GUILayoutUtility.GetRect(new GUIContent(btnLabel),
                    EditorStyles.toolbarDropDown, GUILayout.ExpandWidth(true));
                if (GUI.Button(dropRect, btnLabel, dropDownWithSmallText))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("— None —"),
                        string.IsNullOrEmpty(evt.functionName), () =>
                        {
                            evt.functionName = "";
                            isDirty = true;
                            Repaint();
                        });
                    menu.AddSeparator("");
                    foreach (var method in discoveredMethods)
                    {
                        var captured = method;
                        bool isSelected = method.MethodName == evt.functionName;
                        menu.AddItem(new GUIContent(method.MenuPath), isSelected, () =>
                        {
                            evt.functionName = captured.MethodName;
                            isDirty = true;
                            Repaint();
                        });
                    }
                    menu.DropDown(dropRect);
                }
                GUI.color = Color.white;

                if (notFound)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.HelpBox(
                        $"\"{evt.functionName}\" was not found on the reference object.",
                        MessageType.Warning);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("", GUILayout.Width(72)); // indent
                }

                // Allow manual override in a small field to the side.
                newFunc = EditorGUILayout.TextField(evt.functionName, GUILayout.Width(90));
            }
            else
            {
                // No reference object — plain text field.
                newFunc = EditorGUILayout.TextField(evt.functionName);
            }

            EditorGUILayout.EndHorizontal();

            // -- Parameters --------------------------------------------------------
            // Highlight the field that matches the discovered method's parameter type.
            GUILayout.Label("Parameters", EditorStyles.miniLabel);

            bool noArgument = activeParamType == null && matchIdx >= 0;
            bool highlightStr = activeParamType == typeof(string);
            bool highlightInt = activeParamType == typeof(int) || (activeParamType?.IsEnum ?? false);
            bool highlightFlt = activeParamType == typeof(float);
            bool highlightObj = activeParamType != null && typeof(Object).IsAssignableFrom(activeParamType);

            bool noHighlight = !(noArgument || highlightStr || highlightInt || highlightFlt || highlightObj);
            
            string newStr = evt.stringParameter;
            int newInt = evt.intParameter;
            float newFloat = evt.floatParameter;
            Object newObj = evt.objectReferenceParameter;
            
            if (noHighlight || highlightStr)
            {
                EditorGUILayout.BeginHorizontal();
                DrawParamLabel("String", highlightStr);
                newStr = EditorGUILayout.TextField(evt.stringParameter);
                EditorGUILayout.EndHorizontal();
            }

            if (noHighlight || highlightInt)
            {
                if (activeParamType?.IsEnum ?? false)
                {
                    EditorGUILayout.BeginHorizontal();
                    DrawParamLabel("Enum", highlightInt);

                    var enumObj = Enum.ToObject(activeParamType, evt.intParameter) as Enum;
                    if (enumObj != null)
                    {
                        enumObj = EditorGUILayout.EnumPopup(enumObj);
                        newInt = Convert.ToInt32(enumObj);
                    }
                    else
                    {
                        newInt = EditorGUILayout.Popup(evt.intParameter, Enum.GetNames(activeParamType));
                    }
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    DrawParamLabel("Int", highlightInt);
                    newInt = EditorGUILayout.IntField(evt.intParameter);
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (noHighlight || highlightFlt)
            {
                EditorGUILayout.BeginHorizontal();
                DrawParamLabel("Float", highlightFlt);
                newFloat = EditorGUILayout.FloatField(evt.floatParameter);
                EditorGUILayout.EndHorizontal();
            }

            if (noHighlight || highlightObj)
            {
                EditorGUILayout.BeginHorizontal();
                DrawParamLabel("Object", highlightObj);
                newObj = EditorGUILayout.ObjectField(evt.objectReferenceParameter, typeof(Object), false);
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(2);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), ColSeparator);
            GUILayout.Space(2);
            
            GUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Options", GUILayout.Width(72));
            evt.messageOptions = (SendMessageOptions)EditorGUILayout.EnumPopup(evt.messageOptions);
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                evt.time                   = newTime;
                evt.functionName           = newFunc;
                evt.stringParameter        = newStr;
                evt.intParameter           = newInt;
                evt.floatParameter         = newFloat;
                evt.objectReferenceParameter = newObj;
                isDirty = true;
                Repaint();
            }
        }

        /// Draws a parameter label; highlights it when it's the active parameter type.
        private static void DrawParamLabel(string label, bool highlight)
        {
            if (highlight) GUI.color = new Color(0.6f, 1f, 0.6f);
            GUILayout.Label(highlight ? $"► {label}" : label, GUILayout.Width(72));
            if (highlight) GUI.color = Color.white;
        }

        // --- Right panel (timeline) -----------------------------------------------
        private void DrawRightPanel(Rect panelRect)
        {
            if (modelImporter == null || workingClips == null || workingClips.Length == 0)
            {
                GUILayout.Space(12);
                GUILayout.Label("No Model loaded.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // -- Timeline toolbar -------------------------------------------------
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Timeline", EditorStyles.boldLabel, GUILayout.Width(70));
            GUILayout.Space(8);
            GUILayout.Label("Zoom", EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUI.BeginChangeCheck();
            float logZoom = GUILayout.HorizontalSlider(Mathf.Log(zoom, 2f),
                Mathf.Log(MinZoom, 2f), Mathf.Log(MaxZoom, 2f), GUILayout.Width(90));
            if (EditorGUI.EndChangeCheck()) SetZoom(Mathf.Pow(2f, logZoom), panelRect.width * 0.5f);
            if (GUILayout.Button(new GUIContent("Fit", "Fit whole clip"), EditorStyles.toolbarButton, GUILayout.Width(28)))
                FrameRange(0f, clipLength);
            if (GUILayout.Button(new GUIContent("Sel", "Frame selected event (F)"), EditorStyles.toolbarButton, GUILayout.Width(28)))
                FrameSelection();
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                $"{workingEvents.Count} event{(workingEvents.Count == 1 ? "" : "s")}",
                EditorStyles.miniLabel);
            GUILayout.Space(4);
            bool showPreview = GUILayout.Toggle(previewVisible, "Preview", EditorStyles.toolbarButton, GUILayout.Width(56));
            if (showPreview != previewVisible)
            {
                previewVisible = showPreview;
                EditorPrefs.SetBool(PrefPreviewVisible, previewVisible);
            }
            EditorGUILayout.EndHorizontal();

            float toolbarH  = EditorStyles.toolbar.fixedHeight;
            float bodyH = panelRect.height - toolbarH;
            float timelineW = panelRect.width;
            float timelineH = Mathf.Max(40f, bodyH - TimelineHintH);
            Rect hintRect = new(0, toolbarH + timelineH, timelineW, TimelineHintH);
            timelineViewWidth = timelineW;

            float maxPreviewSize = Mathf.Max(MinPreviewSize,
                Mathf.Min(timelineW - PreviewMargin * 2f, timelineH - 18f - TickRowHeight));
            float displayedPreviewSize = Mathf.Clamp(previewSize, MinPreviewSize, maxPreviewSize);
            Rect previewRect = new(timelineW - displayedPreviewSize - PreviewMargin,
                hintRect.y - displayedPreviewSize - 18f, displayedPreviewSize, displayedPreviewSize);

            // Build function groups in first-seen order so the layout stays stable
            // as events are added / removed.
            var functionOrder = new List<string>();
            var groupedIndices = new Dictionary<string, List<int>>();
            for (int i = 0; i < workingEvents.Count; i++)
            {
                string fn = workingEvents[i].functionName ?? "";
                if (!groupedIndices.ContainsKey(fn))
                {
                    functionOrder.Add(fn);
                    groupedIndices[fn] = new List<int>();
                }
                groupedIndices[fn].Add(i);
            }

            int rowCount    = Mathf.Max(1, functionOrder.Count);
            float contentW  = ContentWidth(timelineViewWidth);
            timelineContentW = contentW;
            float contentH  = TickRowHeight + rowCount * FunctionRowHeight + 4f;
            float scrollH   = Mathf.Max(contentH + 16f, timelineH);

            Rect scrollViewRect = new(0, toolbarH, timelineW, timelineH);
            Event currentEvent = Event.current;
            Vector2 savedMousePosition = currentEvent.mousePosition;
            bool previewHovered = previewVisible && previewRect.Contains(savedMousePosition);
            if (previewHovered) currentEvent.mousePosition = new Vector2(-10000f, -10000f);

            HandleTimelineInput(scrollViewRect, contentH + 1f > timelineH);
            timelineScroll = GUI.BeginScrollView(
                scrollViewRect, timelineScroll,
                new Rect(0, 0, contentW, scrollH - 15f));

            // Background
            EditorGUI.DrawRect(new Rect(0, 0, contentW, contentH), ColBackground);

            // Frame tick ruler (faint vertical grid lines extend through all rows)
            DrawFrameTicks(contentH);
            
            if (functionOrder.Count == 0)
            {
                // No events yet — single placeholder row
                EditorGUI.DrawRect(new Rect(0, TickRowHeight, contentW, FunctionRowHeight), ColEventRow);
                EditorGUI.LabelField(
                    new Rect(timelineScroll.x + 4, TickRowHeight + (FunctionRowHeight - 14) * 0.5f, 260, 14),
                    "Double-click the timeline to add an event", EditorStyles.centeredGreyMiniLabel);

                if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                    && Event.current.clickCount == 2
                    && new Rect(0, TickRowHeight, contentW, FunctionRowHeight)
                        .Contains(Event.current.mousePosition))
                {
                    AddEventAtX(Event.current.mousePosition.x);
                    Event.current.Use();
                }
            }
            else
            {
                float rowY = TickRowHeight;
                for (int gi = 0; gi < functionOrder.Count; gi++)
                {
                    string fn = functionOrder[gi];
                    DrawFunctionRow(gi, fn, groupedIndices[fn], rowY, contentW);
                    rowY += FunctionRowHeight;
                }
            }
            
            // Draw Time Scrubber:
            DrawTimeScrubber(panelRect, functionOrder.Count);

            HandleEventDrag();
            
            GUI.EndScrollView();
            currentEvent.mousePosition = savedMousePosition;

            EditorGUI.DrawRect(hintRect, ColBackground);
            GUI.Label(hintRect,
                "Ctrl+wheel: zoom | wheel: pan | MMB/Alt+drag: pan | F: frame | arrows: step | Home/End | Ctrl+drag: snap | double-click: add event",
                previewHintStyle);

            if (previewVisible) DrawPreviewPane(previewRect);
        }

        private float ContentWidth(float viewWidth) =>
            Mathf.Max(frameCount * FrameWidth + TimelineBorder * 2f + 20f, viewWidth + 1f);

        private void ClampScroll()
        {
            float maxX = Mathf.Max(0f, ContentWidth(timelineViewWidth) - timelineViewWidth);
            timelineScroll.x = Mathf.Clamp(timelineScroll.x, 0f, maxX);
            timelineScroll.y = Mathf.Max(0f, timelineScroll.y);
        }

        private void SetZoom(float newZoom, float anchorViewX)
        {
            newZoom = Mathf.Clamp(newZoom, MinZoom, MaxZoom);
            if (Mathf.Approximately(newZoom, zoom)) return;

            float anchorTime = XToTime(anchorViewX + timelineScroll.x);
            zoom = newZoom;
            timelineScroll.x = TimeToX(anchorTime) - anchorViewX;
            ClampScroll();
            Repaint();
        }

        private void FrameRange(float startTime, float endTime)
        {
            float span = endTime - startTime;
            if (frameRate <= 0f || span <= 0f)
            {
                zoom = 1f;
                timelineScroll.x = 0f;
                return;
            }

            float availableWidth = Mathf.Max(50f, timelineViewWidth - TimelineBorder * 2f);
            zoom = Mathf.Clamp(availableWidth / (span * frameRate) / BaseFrameWidth, MinZoom, MaxZoom);
            timelineScroll.x = TimeToX(startTime) - TimelineBorder;
            ClampScroll();
            Repaint();
        }

        private void FrameSelection()
        {
            if (selectedEventIndex >= 0 && selectedEventIndex < workingEvents.Count && frameRate > 0f)
            {
                float time = workingEvents[selectedEventIndex].time;
                float halfSpan = 15f / frameRate;
                FrameRange(time - halfSpan, time + halfSpan);
            }
            else FrameRange(0f, clipLength);
        }

        private void EnsureTimeVisible(float time)
        {
            if (timelineViewWidth <= 0f) return;

            const float margin = 24f;
            float x = TimeToX(time);
            if (x < timelineScroll.x + margin) timelineScroll.x = x - margin;
            else if (x > timelineScroll.x + timelineViewWidth - margin)
                timelineScroll.x = x - timelineViewWidth + margin;
            ClampScroll();
        }

        private void SetScrubberTime(float time)
        {
            previewPlaying = false;
            currentTimeScrubberTime = Mathf.Clamp(time, 0f, clipLength);
            EnsureTimeVisible(currentTimeScrubberTime);
            Repaint();
        }

        private void HandleTimelineInput(Rect viewRect, bool canScrollVertically)
        {
            Event e = Event.current;
            bool inView = viewRect.Contains(e.mousePosition);

            switch (e.type)
            {
                case EventType.ScrollWheel when inView:
                    if (e.control || e.command)
                    {
                        SetZoom(zoom * Mathf.Exp(-e.delta.y * 0.05f), e.mousePosition.x - viewRect.x);
                        e.Use();
                    }
                    else if (!canScrollVertically)
                    {
                        timelineScroll.x += (e.delta.y + e.delta.x) * 20f;
                        ClampScroll();
                        e.Use();
                        Repaint();
                    }
                    break;
                case EventType.MouseDown when inView && (e.button == 2 || (e.button == 0 && e.alt)):
                    isPanning = true;
                    e.Use();
                    break;
                case EventType.MouseDrag when isPanning:
                    timelineScroll.x -= e.delta.x;
                    timelineScroll.y -= e.delta.y;
                    ClampScroll();
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when isPanning:
                    isPanning = false;
                    e.Use();
                    break;
                case EventType.KeyDown when !EditorGUIUtility.editingTextField:
                    HandleTimelineKeys(e);
                    break;
                case EventType.Repaint when isScrubberDragging || isDraggingEvent:
                    AutoScrollWhileDragging();
                    break;
            }
        }

        private void HandleTimelineKeys(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.F:
                    FrameSelection();
                    e.Use();
                    break;
                case KeyCode.LeftArrow:
                    SetScrubberTime(FrameToTime(TimeToFrame(currentTimeScrubberTime) - (e.shift ? 10 : 1)));
                    e.Use();
                    break;
                case KeyCode.RightArrow:
                    SetScrubberTime(FrameToTime(TimeToFrame(currentTimeScrubberTime) + (e.shift ? 10 : 1)));
                    e.Use();
                    break;
                case KeyCode.Home:
                    SetScrubberTime(0f);
                    e.Use();
                    break;
                case KeyCode.End:
                    SetScrubberTime(clipLength);
                    e.Use();
                    break;
            }
        }

        private void AutoScrollWhileDragging()
        {
            const float edge = 24f;
            float deltaX = 0f;
            if (lastDragViewX < edge) deltaX = lastDragViewX - edge;
            else if (lastDragViewX > timelineViewWidth - edge)
                deltaX = lastDragViewX - (timelineViewWidth - edge);
            if (deltaX == 0f) return;

            float previousScroll = timelineScroll.x;
            timelineScroll.x += Mathf.Clamp(deltaX * 0.5f, -30f, 30f);
            ClampScroll();
            if (Mathf.Approximately(previousScroll, timelineScroll.x)) return;

            ApplyDragTime(lastDragViewX + timelineScroll.x);
            Repaint();
        }

        private void HandlePreviewResizeGrip(Rect rect)
        {
            Rect grip = new(rect.x, rect.y, PreviewGripSize, PreviewGripSize);
            for (int i = 0; i < 3; i++)
                EditorGUI.DrawRect(new Rect(rect.x + 2f + i * 3f, rect.y + 9f - i * 3f, 2f, 2f),
                    new Color(1f, 1f, 1f, 0.5f));
            EditorGUIUtility.AddCursorRect(grip, MouseCursor.ResizeUpLeft);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 0 && grip.Contains(e.mousePosition):
                    GUIUtility.hotControl = id;
                    e.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    previewSize = Mathf.Max(MinPreviewSize,
                        Mathf.Max(rect.xMax - e.mousePosition.x, rect.yMax - e.mousePosition.y));
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    EditorPrefs.SetFloat(PrefPreviewSize, previewSize);
                    e.Use();
                    break;
            }
        }

        private void DrawPreviewPane(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f));
            if (previewClip == null || _previewUtility == null || previewSceneObject == null)
            {
                GUI.Label(rect, previewClip == null ? "No preview clip." : "Preview unavailable.",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            RenderPreviewScene(rect);
            HandlePreviewResizeGrip(rect);

            float x = rect.x + PreviewGripSize + 4f;
            float y = rect.y + 4f;
            if (GUI.Button(new Rect(x, y, 44, 18), previewPlaying ? "Pause" : "Play", EditorStyles.miniButton))
                TogglePreviewPlay();
            x += 48f;
            previewLoop = GUI.Toggle(new Rect(x, y, 44, 18), previewLoop, "Loop", EditorStyles.miniButton);
            x += 48f;
            if (GUI.Button(new Rect(x, y, 48, 18), new GUIContent("Reset", "Reset camera (or double-click)"),
                    EditorStyles.miniButton)) ResetPreviewCamera();

            GUI.Label(new Rect(rect.x + 6f, rect.yMax - 18f, rect.width * 0.4f, 16f),
                $"{currentTimeScrubberTime:F3}s  f{TimeToFrame(currentTimeScrubberTime)}", EditorStyles.whiteMiniLabel);
            GUI.Label(new Rect(rect.xMax - rect.width * 0.6f - 6f, rect.yMax - 18f, rect.width * 0.6f, 16f),
                "drag: rotate  wheel: zoom", previewHintStyle);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Orbit);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 0 && rect.Contains(e.mousePosition):
                    if (e.clickCount == 2) ResetPreviewCamera();
                    else GUIUtility.hotControl = id;
                    e.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    previewYaw += e.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch + e.delta.y * 0.5f, -80f, 80f);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
                case EventType.ScrollWheel when rect.Contains(e.mousePosition):
                    previewZoom = Mathf.Clamp(previewZoom * Mathf.Exp(e.delta.y * 0.05f), 0.1f, 5f);
                    e.Use();
                    Repaint();
                    break;
            }
        }

        private void TogglePreviewPlay()
        {
            previewPlaying = !previewPlaying;
            if (previewPlaying)
            {
                if (currentTimeScrubberTime >= clipLength - 0.0001f) currentTimeScrubberTime = 0f;
                lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        private void RenderPreviewScene(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (previewSceneObject == null) return;
            if (_previewUtility == null) return;
            _previewUtility.BeginPreview(rect, GUIStyle.none);
            previewClip.SampleAnimation(previewSceneObject, currentTimeScrubberTime);
            UpdatePreviewCamera(rect.width / Mathf.Max(1f, rect.height));
            _previewUtility.camera.Render();
            Texture resultTexture = _previewUtility.EndPreview();
            GUI.DrawTexture(rect, resultTexture, ScaleMode.StretchToFill, false);
        }
        
        
        private void DrawFrameTicks(float totalContentH = -1)
        {
            if (frameCount <= 0 || frameRate <= 0) return;

            int step = NiceTickSteps[NiceTickSteps.Length - 1];
            foreach (int candidate in NiceTickSteps)
            {
                if (candidate * FrameWidth >= 36f)
                {
                    step = candidate;
                    break;
                }
            }
            float gridH = totalContentH > 0 ? totalContentH : TickRowHeight;
            bool drawMinor = FrameWidth >= 3f;

            int firstFrame = Mathf.Clamp(Mathf.FloorToInt((timelineScroll.x - TimelineBorder) / FrameWidth) - 1,
                0, frameCount);
            int lastFrame = Mathf.Clamp(Mathf.CeilToInt((timelineScroll.x + timelineViewWidth - TimelineBorder) / FrameWidth) + 1,
                0, frameCount);

            for (int f = firstFrame; f <= lastFrame; f++)
            {
                float x      = TimelineBorder + f * FrameWidth;
                bool  isMajor = f % step == 0;

                if (isMajor)
                {
                    // Faint grid line extending through all rows
                    EditorGUI.DrawRect(new Rect(x, 0, 1f, gridH),
                        new Color(ColTickLine.r, ColTickLine.g, ColTickLine.b, 0.12f));
                    // Solid tick in ruler
                    EditorGUI.DrawRect(new Rect(x, 0, 1f, TickRowHeight), ColTickLine);
                    EditorGUI.LabelField(
                        new Rect(x + 2, 2, 40, 14), f.ToString(), EditorStyles.miniLabel);
                }
                else if (drawMinor)
                {
                    EditorGUI.DrawRect(new Rect(x, TickRowHeight - 7, 1f, 7f), ColTickMinor);
                }
            }
        }


        private string GetEventParameterLabel(AnimationEvent evt)
        {
            int matchIdx = discoveredMethods.FindIndex(m => m.MethodName == evt.functionName);
            Type activeParamType = matchIdx >= 0 ? discoveredMethods[matchIdx].ParamType : null;
            
            if(activeParamType != null && activeParamType.IsEnum)
            {
                var value = evt.intParameter;
                return Enum.GetName(activeParamType, value);
            }
            else if (activeParamType == typeof(int))
            {
                return evt.intParameter.ToString();
            }
            else if (activeParamType == typeof(float))
            {
                return evt.floatParameter.ToString("0.###");
            }
            else if (activeParamType == typeof(string))
            {
                return evt.stringParameter;
            }
            else if (activeParamType == typeof(Object))
            {
                return evt.objectReferenceParameter != null ? evt.objectReferenceParameter.name : "None";
            }

            return "";
        }

        private string GetEventStringName(AnimationEvent evt, string empty = "?")
        {
            bool hasFunc  = !string.IsNullOrEmpty(evt.functionName);
            return hasFunc ? evt.functionName + $"({GetEventParameterLabel(evt)})" : empty;
        }
        
        private void DrawFunctionRow(int groupIndex, string funcName, List<int> eventIndices,
                                      float rowY, float contentW)
        {
            float subRowH = FunctionRowHeight * 0.5f;

            // -- Row background (alternating shades) ------------------------------
            Color bgColor = groupIndex % 2 == 0
                ? ColEventRow
                : new Color(0.20f, 0.20f, 0.20f);
            // EditorGUI.DrawRect(new Rect(0, rowY, contentW, FunctionRowHeight), bgColor);
            // Bottom separator
            EditorGUI.DrawRect(new Rect(0, rowY + FunctionRowHeight - 1, contentW, 1), ColSeparator);

            // -- Sticky function-name label (follows horizontal scroll) ------------
            string displayName = string.IsNullOrEmpty(funcName) ? "(no function)" : funcName;
            Vector2 labelSz    = EditorStyles.miniLabel.CalcSize(new GUIContent(displayName));
            float   labelX     = timelineScroll.x + 2f;
            float   labelY     = rowY + (FunctionRowHeight - labelSz.y) * 0.5f;

            EditorGUI.DrawRect(new Rect(labelX, rowY, /*labelSz.x + 8f*/contentW, FunctionRowHeight),
                new Color(0f, 0f, 0f, 0.35f));
            GUI.color = string.IsNullOrEmpty(funcName)
                ? new Color(1f, 0.5f, 0.5f)
                : new Color(0.72f, 0.72f, 0.72f);
            EditorGUI.LabelField(
                new Rect(labelX + 4f, labelY, labelSz.x + 4f, labelSz.y),
                displayName, EditorStyles.miniLabel);
            GUI.color = Color.white;

            // -- Sub-row assignment (greedy first-free-slot to minimise overlap) ---
            // Sort events in this row by time, then assign each to whichever
            // sub-row's label has already cleared that x position.
            var sorted = eventIndices.OrderBy(i => workingEvents[i].time).ToList();
            float[] nextFreeX = { float.MinValue, float.MinValue }; // per sub-row
            var     subRowMap = new int[eventIndices.Count];         // index → sub-row (0/1)

            for (int s = 0; s < sorted.Count; s++)
            {
                float evtX    = TimeToX(workingEvents[sorted[s]].time);
                string param  = GetEventParameterLabel(workingEvents[sorted[s]]);
                float  paramW = EditorStyles.miniLabel.CalcSize(new GUIContent(param)).x + 16f;

                bool top0 = evtX > nextFreeX[0];
                bool top1 = evtX > nextFreeX[1];
                int  sr   = (top0 && top1) ? 0           // both free → prefer top
                          : top0           ? 0
                          : top1           ? 1
                          : nextFreeX[0] <= nextFreeX[1] ? 0 : 1; // both busy → least busy

                nextFreeX[sr] = evtX + paramW;
                subRowMap[s]  = sr;
            }

            // -- Draw markers -----------------------------------------------------
            for (int s = 0; s < sorted.Count; s++)
            {
                int   idx     = sorted[s];
                float subRowY = rowY + subRowMap[s] * subRowH;
                DrawEventMarkerInRow(idx, subRowY, subRowH);
            }

            // -- Double-click in this row → add event with row's function pre-filled
            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && Event.current.clickCount == 2)
            {
                Rect rowRect = new(0, rowY, contentW, FunctionRowHeight);
                if (rowRect.Contains(Event.current.mousePosition))
                {
                    float t = Mathf.Clamp(XToTime(Event.current.mousePosition.x),
                        0f, clipLength > 0 ? clipLength : float.MaxValue);
                    var newEvt = new AnimationEvent { time = t, functionName = funcName };
                    workingEvents.Add(newEvt);
                    workingEvents.Sort((a, b) => a.time.CompareTo(b.time));
                    selectedEventIndex = workingEvents.IndexOf(newEvt);
                    isDirty = true;
                    Event.current.Use();
                    Repaint();
                }
            }
        }


        private void DrawTimeScrubber(Rect panelRect, int functionOrderCount)
        {
            // EditorGUI.DrawRect(new Rect(0, 0, 1, TickRowHeight), ColTimeScrubberNormal);
            
            float x = TimeToX(currentTimeScrubberTime);
            
            EditorGUI.DrawRect(new Rect(x - 0.5f, 0, 1f, TickRowHeight + FunctionRowHeight * functionOrderCount),
                ColTimeScrubberNormal);

            const float pinW = 8f;
            const float pinH = 10f;
            Rect hitRect = new(x - pinW*0.5f, 0, pinW, pinH);
            EditorGUIUtility.AddCursorRect(hitRect, MouseCursor.SlideArrow);

            var hover = hitRect.Contains(Event.current.mousePosition);
            
            EditorGUI.DrawRect(hitRect, hover ? ColTimeScrubberHovered : ColTimeScrubberNormal);
                
            
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover)
            {
                isScrubberDragging = true;
                GUI.FocusControl(null);
                Event.current.Use();
            }

            if (hover)
            {
                Repaint();
            }
            
            // EditorGUI.DrawRect(new Rect(0, 0, panelRect.width, TickRowHeight), ColTimeScrubberNormal);

            var timeLineHitRect = new Rect(0, 4, timelineContentW, TickRowHeight - 8);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                && !Event.current.alt && timeLineHitRect.Contains(Event.current.mousePosition))
            {
                isScrubberDragging = true;
                previewPlaying = false;
                GUI.FocusControl(null);
                lastDragViewX = Event.current.mousePosition.x - timelineScroll.x;
                lastDragSnap = Event.current.control;
                ApplyDragTime(Event.current.mousePosition.x);
                Event.current.Use();
                Repaint();
            }

            // var normalizedTimerScrubber = Mathf.InverseLerp()
        }
        
        private void DrawEventMarkerInRow(int index, float subRowY, float subRowH)
        {
            var  evt      = workingEvents[index];
            bool selected = index == selectedEventIndex;
            bool hasFunc  = !string.IsNullOrEmpty(evt.functionName);
            float x       = TimeToX(evt.time);

            Color col = selected ? ColEventSelected : hasFunc ? ColEventNormal : ColEventNoFunc;

            // Faint vertical line from tick ruler to the sub-row centre
            float lineBottom = subRowY + subRowH * 0.5f;
            EditorGUI.DrawRect(new Rect(x - 0.5f, 0, 1f, lineBottom),
                new Color(col.r, col.g, col.b, 0.28f));

            // Marker pin centred in sub-row
            const float pinW = 8f;
            const float pinH = 10f;
            float pinX = x - pinW * 0.5f;
            float pinY = subRowY + (subRowH - pinH) * 0.5f;

            if (selected)
                EditorGUI.DrawRect(new Rect(pinX - 1, pinY - 1, pinW + 2, pinH + 2),
                    new Color(1f, 1f, 1f, 0.4f));
            EditorGUI.DrawRect(new Rect(pinX, pinY, pinW, pinH), col);

            // Label: show only the parameter value (function name is the row header).
            // When there's no parameter, show a small dot so the marker isn't bare.
            string paramLabel = GetEventParameterLabel(evt);
            if (string.IsNullOrEmpty(paramLabel)) paramLabel = "·";

            Vector2 sz       = EditorStyles.miniLabel.CalcSize(new GUIContent(paramLabel));
            Rect    labelRect = new(x + pinW * 0.5f + 2f,
                subRowY + (subRowH - sz.y) * 0.5f, sz.x + 2f, sz.y);

            if (selected)
                EditorGUI.DrawRect(labelRect, new Color(0, 0, 0, 0.7f));

            GUI.color = selected ? Color.yellow : hasFunc ? Color.white : new Color(1f, 0.5f, 0.5f);
            EditorGUI.LabelField(labelRect, paramLabel, EditorStyles.miniLabel);
            GUI.color = Color.white;

            // Hit area: full sub-row height for easy clicking
            Rect hitRect = new(x - 8f, subRowY, 16f, subRowH);
            EditorGUIUtility.AddCursorRect(hitRect, MouseCursor.MoveArrow);

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                && hitRect.Contains(Event.current.mousePosition))
            {
                selectedEventIndex = index;
                isDraggingEvent    = true;
                dragEventIndex     = index;
                previewPlaying     = false;
                lastDragViewX      = Event.current.mousePosition.x - timelineScroll.x;
                lastDragSnap       = Event.current.control;
                EnsureTimeVisible(evt.time);
                GUI.FocusControl(null);
                Event.current.Use();
                Repaint();
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 1
                && hitRect.Contains(Event.current.mousePosition))
            {
                int capturedIndex = index;
                var menu          = new GenericMenu();
                menu.AddItem(new GUIContent("Delete Event"), false, () =>
                {
                    workingEvents.RemoveAt(capturedIndex);
                    if (selectedEventIndex >= workingEvents.Count)
                        selectedEventIndex = workingEvents.Count - 1;
                    isDirty = true;
                    Repaint();
                });
                menu.ShowAsContext();
                Event.current.Use();
            }
        }

        private void HandleEventDrag()
        {
            Event e = Event.current;

            if (e.type == EventType.MouseDrag && (isDraggingEvent || isScrubberDragging))
            {
                lastDragViewX = e.mousePosition.x - timelineScroll.x;
                lastDragSnap = e.control;
                ApplyDragTime(e.mousePosition.x);
                e.Use();
                Repaint();
            }

            if (e.type == EventType.MouseUp && (isDraggingEvent || isScrubberDragging))
            {
                isScrubberDragging = false;
                isDraggingEvent = false;
                dragEventIndex = -1;
                e.Use();
            }
        }

        private void ApplyDragTime(float contentX)
        {
            float time = Mathf.Clamp(XToTime(contentX), 0f, clipLength > 0 ? clipLength : float.MaxValue);
            if (lastDragSnap) time = FrameToTime(TimeToFrame(time));

            if (isDraggingEvent && dragEventIndex >= 0 && dragEventIndex < workingEvents.Count)
            {
                workingEvents[dragEventIndex].time = time;
                currentTimeScrubberTime = time;
                isDirty = true;
            }
            else if (isScrubberDragging)
            {
                currentTimeScrubberTime = time;
            }
        }

        // --- Event add / remove ---------------------------------------------------
        private void AddEvent()
        {
            if (workingClips == null || workingClips.Length == 0) return;

            float t = clipLength > 0 ? clipLength * 0.5f : 0f;
            AddEventAtTime(t);
        }

        private void AddEventAtX(float x)
        {
            float t = Mathf.Clamp(XToTime(x), 0f, clipLength > 0 ? clipLength : float.MaxValue);
            AddEventAtTime(t);
        }

        private void AddEventAtTime(float time)
        {
            var evt = new AnimationEvent
            {
                time         = time,
                functionName = "OnAnimationEvent",
            };

            workingEvents.Add(evt);
            workingEvents.Sort((a, b) => a.time.CompareTo(b.time));
            selectedEventIndex = workingEvents.IndexOf(evt);
            isDirty            = true;
            Repaint();
        }

        private void RemoveSelectedEvent()
        {
            if (selectedEventIndex < 0 || selectedEventIndex >= workingEvents.Count) return;

            workingEvents.RemoveAt(selectedEventIndex);
            selectedEventIndex = Mathf.Clamp(selectedEventIndex - 1, -1, workingEvents.Count - 1);
            isDirty            = true;
            Repaint();
        }

        // --- Coordinate helpers ---------------------------------------------------
        private float TimeToX(float time) => TimelineBorder + time * frameRate * FrameWidth;

        private float XToTime(float x) => frameRate > 0
            ? (x - TimelineBorder) / (frameRate * FrameWidth)
            : 0f;
        
        private int TimeToFrame(float time) => Mathf.Clamp(Mathf.RoundToInt(time * frameRate), 0, frameCount);
        private float FrameToTime(int frame) => frameRate > 0 ? frame / frameRate : 0f;

        // --- Favorites panel ------------------------------------------------------
        private void DrawFavoritesPanel()
        {
            // Header row
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Favorites", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            // [+] add current Model
            GUI.enabled = selectedModel != null && !FavoriteModels.Contains(selectedModel);
            if (GUILayout.Button(EditorGUIUtility.IconContent("Toolbar Plus"), EditorStyles.toolbarButton,
                    GUILayout.Width(22)))
            {
                FavoriteModels.Add(selectedModel);
                AnimationEventProjectPrefs.instance.SavePrefs();
                Repaint();
            }
            GUI.enabled = true;

            // [-] remove selected
            GUI.enabled = selectedFavIndex >= 0 && selectedFavIndex < FavoriteModels.Count;
            if (GUILayout.Button(EditorGUIUtility.IconContent("Toolbar Minus"), EditorStyles.toolbarButton,
                    GUILayout.Width(22)))
            {
                Undo.RecordObject(AnimationEventProjectPrefs.instance, "Remove Favorite");
                FavoriteModels.RemoveAt(selectedFavIndex);
                selectedFavIndex = Mathf.Clamp(selectedFavIndex - 1, -1, FavoriteModels.Count - 1);
                AnimationEventProjectPrefs.instance.SavePrefs();
                Repaint();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // List
            favScroll = EditorGUILayout.BeginScrollView(favScroll);

            for (int i = 0; i < FavoriteModels.Count; i++)
            {
                var model  = FavoriteModels[i];
                bool isLoaded  = model == selectedModel;
                bool isSelected = i == selectedFavIndex;

                Rect row = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight + 3,
                    GUILayout.ExpandWidth(true));

                Color rowColor = isLoaded  ? new Color(0.25f, 0.45f, 0.25f) :
                                 isSelected ? new Color(0.24f, 0.37f, 0.59f) :
                                 i % 2 == 0 ? new Color(0.21f, 0.21f, 0.21f) :
                                              new Color(0.18f, 0.18f, 0.18f);
                EditorGUI.DrawRect(row, rowColor);

                // Loaded indicator dot
                if (isLoaded)
                    EditorGUI.DrawRect(new Rect(row.x + 2, row.y + 5, 5, 5), new Color(0.4f, 1f, 0.4f));

                string displayName = model != null ? model.name : "— missing —";
                GUI.color = model != null ? Color.white : new Color(1f, 0.5f, 0.5f);
                GUI.Label(new Rect(row.x + 11, row.y + 2, row.width - 13, row.height - 2),
                    displayName, EditorStyles.miniLabel);
                GUI.color = Color.white;

                // Single-click: select + load
                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    selectedFavIndex = i;
                    if (model != null) TrySetModel(model);
                    GUI.FocusControl(null);
                    Event.current.Use();
                    Repaint();
                }

                // Right-click context menu
                if (Event.current.type == EventType.ContextClick && row.Contains(Event.current.mousePosition))
                {
                    int ci = i;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Load"), false, () => { if (FavoriteModels[ci] != null) TrySetModel(FavoriteModels[ci]); });
                    menu.AddItem(new GUIContent("Remove from Favorites"), false, () =>
                    {
                        FavoriteModels.RemoveAt(ci);
                        if (selectedFavIndex >= FavoriteModels.Count) selectedFavIndex = FavoriteModels.Count - 1;
                        AnimationEventProjectPrefs.instance.SavePrefs();
                        Repaint();
                    });
                    menu.ShowAsContext();
                    Event.current.Use();
                }
            }

            EditorGUILayout.EndScrollView();

            // Drag-and-drop model assets onto the panel to add them
            Rect dropArea = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(dropArea, new Color(0f, 0f, 0f, 0.2f));
            GUI.Label(dropArea, "Drop Model here", EditorStyles.centeredGreyMiniLabel);

            var dropEvt = Event.current;
            if ((dropEvt.type == EventType.DragUpdated || dropEvt.type == EventType.DragPerform)
                && dropArea.Contains(dropEvt.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (dropEvt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj is GameObject go)
                        {
                            string p = AssetDatabase.GetAssetPath(go);
                            // TODO: allow all kinds of animations not just fbx
                            if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                                && !FavoriteModels.Contains(go))
                            {
                                FavoriteModels.Add(go);
                            }
                        }
                    }
                    AnimationEventProjectPrefs.instance.SavePrefs();
                    Repaint();
                }
                dropEvt.Use();
            }
        }

        // --- Splitter -------------------------------------------------------------
        private void HandleFavSplitterResize(Rect splitterRect)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
            {
                isFavResizing = true;
                Event.current.Use();
            }

            if (isFavResizing)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    FavPanelWidth = Mathf.Clamp(Event.current.mousePosition.x,
                        MinFavWidth, position.width - MinSplit - MinRightWidth - 8f);
                    Repaint();
                    Event.current.Use();
                }

                if (Event.current.type == EventType.MouseUp)
                {
                    isFavResizing = false;
                    AnimationEventEditorPrefs.instance.SavePrefs(); // persist final width
                }
            }
        }

        private void HandleSplitterResize(Rect splitterRect, float leftOffset = 0f)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                Event.current.Use();
            }

            if (isResizing)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    split = Mathf.Clamp(Event.current.mousePosition.x - leftOffset,
                        MinSplit, position.width - leftOffset - MinRightWidth);
                    Repaint();
                    Event.current.Use();
                }

                if (Event.current.type == EventType.MouseUp)
                    isResizing = false;
            }
        }
    }
}

