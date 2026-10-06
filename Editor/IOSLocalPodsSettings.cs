using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace iDreams.IOSLocalPods
{
  /// <summary>
  /// Per-project configuration, stored in ProjectSettings/IOSLocalPodsSettings.json (check it in).
  /// </summary>
  [Serializable]
  public class IOSLocalPodsSettings
  {
    [Serializable]
    public class cPod
    {
      public string _Name = "";
      // CocoaPods requirement ("~> 13.9", "3.1.0", ">= 2.0, < 3"); empty = newest.
      public string _Requirement = "";
    }

    public const string FilePath = "ProjectSettings/IOSLocalPodsSettings.json";

    // Relative to the project root, outside Assets so Unity does not import the frameworks.
    public string _PodsDirectory = "iOSPods";
    // Empty = PlayerSettings.iOS.targetOSVersionString.
    public string _DeploymentTarget = "";
    // Force EDM's Podfile generation, Workspace integration and pod install on every iOS build.
    public bool _EnforceCocoaPodsSettings = true;
    // On iOS builds, offer to generate missing local pods (or set them up from *Dependencies.xml).
    public bool _CheckOnBuild = true;
    public List<cPod> _Pods = new List<cPod>();

    private static IOSLocalPodsSettings m_Instance;

    public static IOSLocalPodsSettings Instance
    {
      get
      {
        if (m_Instance == null)
        {
          m_Instance = Load();
        }
        return m_Instance;
      }
    }

    public string ResolvedDeploymentTarget
    {
      get { return string.IsNullOrEmpty(_DeploymentTarget) ? PlayerSettings.iOS.targetOSVersionString : _DeploymentTarget.Trim(); }
    }

    public IEnumerable<cPod> ValidPods
    {
      get { return _Pods.Where(p => !string.IsNullOrEmpty(p._Name)); }
    }

    public string PodDirectory(string name)
    {
      return _PodsDirectory.TrimEnd('/', '\\') + "/" + name;
    }

    public string PodspecPath(string name)
    {
      return Path.Combine(PodDirectory(name), name + ".podspec.json");
    }

    public static IOSLocalPodsSettings Reload()
    {
      m_Instance = Load();
      return m_Instance;
    }

    public void Save()
    {
      File.WriteAllText(FilePath, JsonUtility.ToJson(this, true) + "\n");
    }

    private static IOSLocalPodsSettings Load()
    {
      if (File.Exists(FilePath))
      {
        try
        {
          return JsonUtility.FromJson<IOSLocalPodsSettings>(File.ReadAllText(FilePath)) ?? new IOSLocalPodsSettings();
        }
        catch (ArgumentException e)
        {
          Debug.LogError("[IOSLocalPods] Cannot read " + FilePath + ": " + e.Message);
        }
      }
      return new IOSLocalPodsSettings();
    }
  }

  internal static class IOSLocalPodsSettingsProvider
  {
    [SettingsProvider]
    private static SettingsProvider Create()
    {
      return new SettingsProvider("Project/iOS Local Pods", SettingsScope.Project)
      {
        label = "iOS Local Pods",
        guiHandler = _ => OnGUI(),
        keywords = new HashSet<string>(new[] { "CocoaPods", "Podfile", "pod", "iOS", "Xcode", "deployment target", "EDM" }),
      };
    }

    private static void OnGUI()
    {
      IOSLocalPodsSettings settings = IOSLocalPodsSettings.Instance;
      bool save = false;

      EditorGUILayout.HelpBox("Pods listed here are served from the pods directory with their podspec's iOS deployment target raised to the one below, "
        + "and their entries in *Dependencies.xml are switched to path=. Run Refresh Local Pods after changing the list.", MessageType.None);

      EditorGUI.BeginChangeCheck();
      settings._PodsDirectory = EditorGUILayout.TextField(new GUIContent("Pods Directory", "Relative to the project root. Check it in to version control."), settings._PodsDirectory);
      settings._DeploymentTarget = EditorGUILayout.TextField(new GUIContent("iOS Deployment Target", "Empty = Player Settings (" + PlayerSettings.iOS.targetOSVersionString + ")."), settings._DeploymentTarget);
      settings._EnforceCocoaPodsSettings = EditorGUILayout.Toggle(new GUIContent("Enforce CocoaPods Settings",
        "On every iOS build turn on EDM's Podfile generation, Workspace integration and pod install. Runs first (callbackOrder 0), so a later preprocessor can still turn them off, e.g. for an offline build."),
        settings._EnforceCocoaPodsSettings);
      settings._CheckOnBuild = EditorGUILayout.Toggle(new GUIContent("Check on iOS Build",
        "When local pods are missing (or none are set up yet), an iOS build asks to generate them: Generate / Cancel Build / Generate + Ignore Folder. Batch mode generates without asking."),
        settings._CheckOnBuild);
      save |= EditorGUI.EndChangeCheck();

      VcsIgnore.cTarget ignore = VcsIgnore.Find(settings._PodsDirectory);
      EditorGUILayout.BeginHorizontal();
      EditorGUILayout.PrefixLabel(new GUIContent("Version Control", "Ignoring the pods directory keeps the downloaded frameworks out of the repository; every machine generates them on its first iOS build."));
      if (ignore == null)
      {
        GUILayout.Label("no Plastic SCM workspace or git repository found", EditorStyles.miniLabel);
      }
      else if (VcsIgnore.IsIgnored(ignore))
      {
        GUILayout.Label(ignore._Pattern + " ignored in " + System.IO.Path.GetFileName(ignore._IgnoreFile) + " (" + ignore._Vcs + ")", EditorStyles.miniLabel);
      }
      else if (GUILayout.Button("Ignore " + ignore._Pattern + " in " + System.IO.Path.GetFileName(ignore._IgnoreFile)))
      {
        string message = VcsIgnore.Add(ignore);
        Debug.Log("[IOSLocalPods] " + message);
        EditorUtility.DisplayDialog("iOS Local Pods", message, "OK");
      }
      EditorGUILayout.EndHorizontal();

      EditorGUILayout.Space();
      EditorGUILayout.LabelField("Pods", EditorStyles.boldLabel);
      int remove = -1;
      for (int i = 0; i < settings._Pods.Count; i++)
      {
        IOSLocalPodsSettings.cPod pod = settings._Pods[i];
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        pod._Name = EditorGUILayout.TextField(pod._Name);
        pod._Requirement = EditorGUILayout.TextField(pod._Requirement, GUILayout.Width(110));
        save |= EditorGUI.EndChangeCheck();
        GUILayout.Label(IOSLocalPods.InstalledVersion(pod._Name) ?? "not installed", EditorStyles.miniLabel, GUILayout.Width(80));
        if (GUILayout.Button("-", GUILayout.Width(22)))
        {
          remove = i;
        }
        EditorGUILayout.EndHorizontal();
      }
      if (remove >= 0)
      {
        settings._Pods.RemoveAt(remove);
        save = true;
      }

      EditorGUILayout.BeginHorizontal();
      if (GUILayout.Button("Add"))
      {
        settings._Pods.Add(new IOSLocalPodsSettings.cPod());
        save = true;
      }
      if (GUILayout.Button("Detect from Dependencies.xml"))
      {
        save |= IOSLocalPods.AddDetectedPods(settings) > 0;
      }
      EditorGUILayout.EndHorizontal();

      if (save)
      {
        settings.Save();
      }

      EditorGUILayout.Space();
      EditorGUILayout.BeginHorizontal();
      if (GUILayout.Button("Refresh Local Pods"))
      {
        EditorApplication.delayCall += IOSLocalPods.RefreshLocalPods;
      }
      if (GUILayout.Button("Apply CocoaPods Settings Now"))
      {
        IOSLocalPods.ApplyCocoaPodsSettings();
      }
      EditorGUILayout.EndHorizontal();
    }
  }
}
