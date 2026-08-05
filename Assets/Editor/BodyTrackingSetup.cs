using UnityEditor;
using UnityEngine;

/// <summary>
/// Enables Meta IOBT body tracking in the project settings.
///
/// These live in serialized assets that are inconvenient to edit by hand, and getting any one of
/// them wrong fails in a misleading way: the app runs, every inspector checkbox looks right, and
/// OVRBody still reports "Failed to start body tracking". Scripted so it is reproducible.
///
///   Unity -batchmode -nographics -projectPath &lt;proj&gt; -buildTarget Android \
///         -executeMethod BodyTrackingSetup.Configure -quit
///
/// or from the editor: Tools > Body Tracking > Configure Project Settings.
/// </summary>
public static class BodyTrackingSetup
{
    [MenuItem("Tools/Body Tracking/Configure Project Settings")]
    public static void Configure()
    {
        ConfigureProjectConfig();
        ConfigureRuntimeSettings();
        // Runs last: it opens and saves scenes, so anything editing scene objects must come before
        // ConfigureSceneBodySource saves, and ConfigurePermissionRequest already opens scenes too.
        ConfigurePermissionRequest();
        ConfigureSceneBodySource();
        AssetDatabase.SaveAssets();
        Debug.Log("[BodyTrackingSetup] done");
    }




    /// <summary>
    /// Single debug APK at Build/quest-body.apk, for iterating on body tracking.
    ///
    /// Deliberately separate from ProjectBuild.Build(): that one produces four APKs
    /// (cn/i18n x debug/release) and swaps region jars, which is release packaging rather than
    /// something to run on every code change. Keep BuildOptions.Development so Debug.Log
    /// survives into logcat.
    /// </summary>
    [MenuItem("Tools/Body Tracking/Build Debug APK")]
    public static void BuildDebugApk()
    {
        var scenes = new System.Collections.Generic.List<string>();
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (s != null && s.enabled)
            {
                scenes.Add(s.path);
            }
        }

        if (scenes.Count == 0)
        {
            Debug.LogError("[BodyTrackingSetup] no enabled scenes in build settings");
            FailBuild();
            return;
        }

        System.IO.Directory.CreateDirectory("Build");
        const string outPath = "Build/quest-body.apk";

        // Uses the project's own application id. A local build is signed with the debug key, so
        // Android will not install it over a release-signed package of the same id -- uninstall
        // the official build first:
        //   adb uninstall com.xrobotoolkit.client.quest
        var summary = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = outPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.Development,
        }).summary;

        Debug.Log($"[BodyTrackingSetup] build {summary.result}: " +
                  $"{summary.totalSize / 1024 / 1024} MB, {summary.totalErrors} errors, " +
                  $"took {summary.totalTime}");

        if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            FailBuild();
        }
    }

    /// <summary>
    /// Signals build failure without taking an interactive editor down with it.
    /// </summary>
    /// <remarks>
    /// A non-zero exit is what a CI runner needs, but calling it from the menu would close the
    /// editor and lose unsaved work, so it is limited to batch mode. Interactively the logged
    /// error is the signal.
    /// </remarks>
    private static void FailBuild()
    {
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(1);
        }
    }

    /// <summary>Quest Features > Body Tracking Support = Supported.</summary>
    private static void ConfigureProjectConfig()
    {
        var cfg = OVRProjectConfig.CachedProjectConfig;
        if (cfg == null)
        {
            Debug.LogError("[BodyTrackingSetup] OVRProjectConfig.CachedProjectConfig == null");
            return;
        }

        cfg.bodyTrackingSupport = OVRProjectConfig.FeatureSupport.Supported;
        OVRProjectConfig.CommitProjectConfig(cfg);
        Debug.Log("[BodyTrackingSetup] bodyTrackingSupport=Supported");
    }

    /// <summary>
    /// Fidelity=High is what actually turns IOBT on; Low is IK inference from headset and
    /// controllers only, not camera-measured limbs.
    ///
    /// JointSet is only the startup default; the Mode dropdown switches it at runtime.
    /// </summary>
    private static void ConfigureRuntimeSettings()
    {
        var rs = OVRRuntimeSettings.GetRuntimeSettings();
        rs.BodyTrackingFidelity = OVRPlugin.BodyTrackingFidelity2.High;
        // The joint set the app starts on, not a fixed choice: the Mode dropdown switches it at
        // runtime via OVRBody.SetRequestedJointSet. UpperBody is the default because its joints are
        // all camera-measured, whereas FullBody's extra 14 are inferred by Generative Legs.
        rs.BodyTrackingJointSet = OVRPlugin.BodyJointSet.UpperBody;
        OVRRuntimeSettings.CommitRuntimeSettings(rs);
        Debug.Log($"[BodyTrackingSetup] fidelity={rs.BodyTrackingFidelity} " +
                  $"jointSet={rs.BodyTrackingJointSet}");
    }

    /// <summary>
    /// OVRManager > Permission Requests On Startup > Body Tracking.
    ///
    /// Without this the app never asks for com.oculus.permission.BODY_TRACKING at runtime, so
    /// tracking is refused even though the manifest declares the permission.
    /// </summary>
    private static void ConfigurePermissionRequest()
    {
        // Scoped to Assets/: an unscoped search also returns scenes shipped inside read-only
        // packages (e.g. Meta's OVRTransitionScene), and opening one of those throws.
        foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                continue;
            }

            var changed = false;
            foreach (var mgr in Object.FindObjectsOfType<OVRManager>())
            {
                // requestBodyTrackingPermissionOnStartup is internal, so reach it via
                // SerializedObject rather than adding a dependency on Meta's internals.
                var so = new SerializedObject(mgr);
                var prop = so.FindProperty("requestBodyTrackingPermissionOnStartup");
                if (prop == null)
                {
                    Debug.LogWarning($"[BodyTrackingSetup] {mgr.name}: property not found");
                    continue;
                }

                if (!prop.boolValue)
                {
                    prop.boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                    Debug.Log($"[BodyTrackingSetup] {path}: {mgr.name} permission request enabled");
                }
            }

            if (changed)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            }
        }
    }

    /// <summary>
    /// Adds the OVRBody component and wires QuestTrackingDataSource.body / .trackingSpace.
    ///
    /// Without an OVRBody in the scene nothing ever requests a tracking session from the runtime,
    /// so BodyState stays null and no BodyMeta is emitted. This failed silently: the build
    /// succeeded, project settings were all correct, and logcat showed no body-tracking errors at
    /// all -- just no output. The component goes on the OVRCameraRig so it shares the rig's
    /// lifetime, and OVRBody's own _providedSkeletonType default (UpperBody) already matches
    /// ConfigureRuntimeSettings.
    /// </summary>
    private static void ConfigureSceneBodySource()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                continue;
            }

            var sources = Object.FindObjectsOfType<QuestTrackingDataSource>();
            if (sources.Length == 0)
            {
                continue;
            }

            var rig = Object.FindObjectOfType<OVRCameraRig>();
            if (rig == null)
            {
                Debug.LogWarning($"[BodyTrackingSetup] {path}: no OVRCameraRig, skipping");
                continue;
            }

            var body = Object.FindObjectOfType<OVRBody>();
            if (body == null)
            {
                body = rig.gameObject.AddComponent<OVRBody>();
                Debug.Log($"[BodyTrackingSetup] {path}: added OVRBody to {rig.name}");
            }

            foreach (var src in sources)
            {
                src.body = body;
                src.trackingSpace = rig.trackingSpace;
                EditorUtility.SetDirty(src);
                Debug.Log($"[BodyTrackingSetup] {path}: {src.name}.body={body.name} " +
                          $"trackingSpace={rig.trackingSpace?.name ?? "null"}");
            }

            ActivateBodyModeUi(path);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }
    }

    /// <summary>
    /// Un-hides the body mode dropdown and every inactive ancestor of it.
    /// </summary>
    /// <remarks>
    /// Upstream ships the "Mode" row deactivated because body tracking was unimplemented on Quest
    /// ("Coming soon"). Wiring the dropdown's listeners is not enough on its own: an inactive
    /// ancestor keeps the control off-screen, so on-device there is simply no dropdown to select
    /// and TrackingType stays None -- which looks identical to a data pipeline that is broken.
    ///
    /// Walks up from the dropdown rather than hardcoding "Mode", since only the ancestor chain is
    /// guaranteed to be what actually gates visibility.
    /// </remarks>
    private static void ActivateBodyModeUi(string path)
    {
        foreach (var ui in Object.FindObjectsOfType<UIOperate>(true))
        {
            var drop = ui.bodyModeDrop;
            if (drop == null)
            {
                Debug.LogWarning($"[BodyTrackingSetup] {path}: {ui.name}.bodyModeDrop unassigned");
                continue;
            }

            for (var t = drop.transform; t != null; t = t.parent)
            {
                if (t.gameObject.activeSelf)
                {
                    continue;
                }

                t.gameObject.SetActive(true);
                EditorUtility.SetDirty(t.gameObject);
                Debug.Log($"[BodyTrackingSetup] {path}: activated '{t.name}'");
            }

            RelabelBodyModeOptions(drop, path);
            WidenBodyModeCaption(drop, path);
            LayOutBodyModeRow(drop, path);
            WidenBodyInfo(ui, path);
        }
    }

    /// <summary>
    /// Gives the Status line room for the full, unabbreviated readout.
    /// </summary>
    /// <remarks>
    /// It was authored at 280px for the old static text ("BodyTracking on (High)"). The live
    /// readout spells out joint count, calibration state and confidence, which does not fit; left
    /// alone the tail is clipped mid-word, and calibration state is the part that gets cut.
    ///
    /// Overflow is enabled as well as widening: the widest string is the "enable it in system
    /// settings" instruction, which no width available in this panel fits, and spilling that rare
    /// case is better than shrinking the text shown constantly.
    /// </remarks>
    private static void WidenBodyInfo(UIOperate ui, string path)
    {
        var info = ui.BodyInfo;
        if (info == null)
        {
            Debug.LogWarning($"[BodyTrackingSetup] {path}: {ui.name}.BodyInfo unassigned");
            return;
        }

        var rt = (RectTransform)info.transform;

        // Pivot and anchor on the left edge before widening. Its parent is a zero-size layout node,
        // so with the authored centre pivot the text grows symmetrically about a point -- widening
        // it pushed half the extra width off the left side of the panel rather than filling the
        // empty space to the right. Anchoring left makes the width grow rightwards only.
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        // Spans from under the "S" of the Status heading to the panel's right edge.
        rt.sizeDelta = new Vector2(265f, rt.sizeDelta.y);
        // Lines the readout up with the "S" of the "Status" heading above it, which is how the
        // other sections in this panel read: "Mode" sits under the "B" of "Body Tracking", and
        // "Send" under the "D" of "Data & Control". The offset is negative because the parent is
        // placed by its own layout group well right of the panel edge, so this walks back left
        // from there rather than indenting in from the left.
        rt.anchoredPosition = new Vector2(-139f, rt.anchoredPosition.y);
        info.horizontalOverflow = HorizontalWrapMode.Overflow;
        info.alignment = TextAnchor.MiddleLeft;
        // Smaller than the panel's labels: it is a readout rather than a control, and spelling the
        // fields out in full does not fit between the dropdown's left edge and the panel's right
        // edge at the authored 14pt. Shrinking the glyphs keeps the words whole, which is the point.
        info.fontSize = 11;
        EditorUtility.SetDirty(info);
        EditorUtility.SetDirty(rt);
        Debug.Log($"[BodyTrackingSetup] {path}: BodyInfo laid out at " +
                  $"x={rt.anchoredPosition.x} width={rt.sizeDelta.x}");
    }

    /// <summary>
    /// Places the mode row inside the panel, and drops the "Coming soon ..." placeholder.
    /// </summary>
    /// <remarks>
    /// The row was authored to sit hidden behind that placeholder, so its layout was never
    /// meaningful: the resolved rect put the "Mode" label at x=-1.15 against a panel whose left
    /// edge is x=-0.86, i.e. the whole control rendered outside the panel, to the left of the
    /// window. Two nested HorizontalLayoutGroups were fighting -- the "Mode" group padded +67
    /// while its "BodyTracking" child padded -267, netting -200.
    ///
    /// Rather than tune those against each other, both are zeroed and the row is aligned on the
    /// Head/Controller row above it, which is the visual reference the operator actually compares
    /// against. Spacing was likewise a large negative number (-623) cancelling the padding.
    ///
    /// Kept in the setup script rather than hand-edited into Main.unity so it stays reproducible,
    /// and re-running it after an upstream merge repairs the row instead of silently regressing.
    /// </remarks>
    private static void LayOutBodyModeRow(UnityEngine.UI.Dropdown drop, string path)
    {
        // dropdown -> BodyTracking (inner group) -> Mode (outer group)
        var inner = drop.transform.parent;
        var outer = inner != null ? inner.parent : null;
        if (inner == null || outer == null)
        {
            Debug.LogWarning($"[BodyTrackingSetup] {path}: unexpected mode row hierarchy");
            return;
        }

        // Matches the Head/Controller row's group, so the "Mode" label lines up with "Head".
        var outerGroup = outer.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        if (outerGroup != null)
        {
            outerGroup.padding = new RectOffset(1, 0, 0, 0);
            outerGroup.spacing = 0;
            outerGroup.childForceExpandWidth = false;
            EditorUtility.SetDirty(outerGroup);
        }

        var innerGroup = inner.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        if (innerGroup != null)
        {
            // Bottom padding lifts the row; the authored 9px of *top* padding pushed it down
            // instead. Measured against the rows this should match: Mode sat 0.034 world units
            // below its box centre where Head/Controller/Send sit 0.007-0.010 below theirs.
            // Zeroing top got it to 0.021, and 8px of bottom padding covers the rest.
            innerGroup.padding = new RectOffset(0, 0, 0, 8);
            innerGroup.spacing = 8;
            innerGroup.childForceExpandWidth = false;
            innerGroup.childAlignment = TextAnchor.MiddleLeft;
            EditorUtility.SetDirty(innerGroup);
        }

        // The label carries a stale offset from when it was positioned by hand; the layout group
        // drives x now, but a non-zero anchoredPosition still shifts it.
        foreach (RectTransform child in inner)
        {
            child.anchoredPosition = new Vector2(0, child.anchoredPosition.y);
            EditorUtility.SetDirty(child);
        }

        HideComingSoonPlaceholder(outer.parent, path);
        Debug.Log($"[BodyTrackingSetup] {path}: mode row re-laid out");
    }

    /// <summary>
    /// Hides the "Coming soon ..." label that upstream showed in place of the body mode control.
    /// </summary>
    /// <remarks>
    /// Matched on its text rather than its name ("Label (1)") because the name carries no meaning
    /// and would silently hide the wrong object if the panel is ever rearranged. Deactivated
    /// rather than deleted so the change stays reversible and merges cleanly.
    /// </remarks>
    private static void HideComingSoonPlaceholder(Transform panel, string path)
    {
        if (panel == null)
        {
            return;
        }

        foreach (var text in panel.GetComponentsInChildren<UnityEngine.UI.Text>(true))
        {
            if (text.text == null || !text.text.StartsWith("Coming soon"))
            {
                continue;
            }

            text.gameObject.SetActive(false);
            EditorUtility.SetDirty(text.gameObject);
            Debug.Log($"[BodyTrackingSetup] {path}: hid placeholder '{text.text}'");
        }
    }

    /// <summary>
    /// Relabels the body mode dropdown to match what the options actually do on Quest.
    /// </summary>
    /// <remarks>
    /// OnBodyModeDrop casts the dropdown *index* to TrackingType, so the labels are decorative
    /// while the index is load-bearing. Upstream inherited PICO's "None / Upper / Full" wording,
    /// where index 2 ("Full") was PICO's external-tracker mode -- no Quest equivalent, so "Full"
    /// read like full-body capture but was a dead end. It now really is full body: index 2 is
    /// TrackingType.FullBody, which switches the runtime to the 84-joint skeleton.
    ///
    /// The joint count is spelled out because it is the one thing that distinguishes the two on
    /// the wire, and it is what the status line reports back once a switch takes effect.
    /// </remarks>
    /// <summary>
    /// Gives the dropdown and its labels room for the mode names.
    /// </summary>
    /// <remarks>
    /// The control was authored 70px wide with 45px Wrap-overflow labels, which fitted the old
    /// "Body (IOBT)" but clips "Upper Body (70)" -- and a label that reads as a different mode is
    /// worse than one that is visibly cut off.
    ///
    /// The control itself has to grow, not just the text inside it: stretching the labels to the
    /// parent only buys 70px, still a few short. Its layout group has ChildControlWidth off and
    /// 461px of row to spend, so widening the rect sticks.
    ///
    /// Widened rather than shrinking the font: at size 10 these are already the smallest text in
    /// the panel.
    /// </remarks>
    private static void WidenBodyModeCaption(UnityEngine.UI.Dropdown drop, string path)
    {
        // Fits "Upper Body (70)" at font size 10 with margin for the arrow, measured from the
        // clipped result rather than guessed: 70px cut the closing bracket.
        const float dropdownWidth = 130f;
        var dropRt = (RectTransform)drop.transform;
        dropRt.sizeDelta = new Vector2(dropdownWidth, dropRt.sizeDelta.y);
        EditorUtility.SetDirty(dropRt);

        // Left inset clearing the item template's checkmark, which is anchored left, 20px wide at
        // x=10 -- so it occupies 0..20px. Taking the label to x=0 to gain width put the text
        // underneath it and the tick overlapped the first letter. The caption has no checkmark and
        // keeps the authored inset, so the two are indented alike.
        const float checkmarkInset = 22f;

        // The caption shows the current selection; the item template is what the open list uses.
        // Fixing only the caption leaves the list itself clipped.
        foreach (var label in new[] { drop.captionText, drop.itemText })
        {
            if (label == null)
            {
                continue;
            }

            var isItem = label == drop.itemText;
            var inset = isItem ? checkmarkInset : 10f;
            var rt = (RectTransform)label.transform;
            // Stretch to the parent's width instead of a fixed size, so this does not need to know
            // how wide the dropdown is -- and stays right if the row is ever re-laid out.
            rt.anchorMin = new Vector2(0f, rt.anchorMin.y);
            rt.anchorMax = new Vector2(1f, rt.anchorMax.y);
            // Negative width leaves room on both sides: the inset on the left, plus the same again
            // on the right so text stops short of the arrow rather than touching it.
            rt.sizeDelta = new Vector2(-(inset + 10f), rt.sizeDelta.y);
            rt.anchoredPosition = new Vector2(inset * 0.5f, rt.anchoredPosition.y);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.alignment = TextAnchor.MiddleLeft;
            EditorUtility.SetDirty(label);
            EditorUtility.SetDirty(rt);
        }

        Debug.Log($"[BodyTrackingSetup] {path}: bodyModeDrop widened to {dropdownWidth}px, caption and items stretched");
    }

    private static void RelabelBodyModeOptions(UnityEngine.UI.Dropdown drop, string path)
    {
        var labels = new[] { "Off", "Upper Body (70)", "Full Body (84)" };
        if (drop.options.Count != labels.Length)
        {
            Debug.LogWarning($"[BodyTrackingSetup] {path}: bodyModeDrop has {drop.options.Count} " +
                             $"options, expected {labels.Length}; leaving labels alone");
            return;
        }

        for (var i = 0; i < labels.Length; i++)
        {
            drop.options[i].text = labels[i];
        }

        drop.RefreshShownValue();
        EditorUtility.SetDirty(drop);
        Debug.Log($"[BodyTrackingSetup] {path}: bodyModeDrop labels -> {string.Join(" / ", labels)}");
    }
}
