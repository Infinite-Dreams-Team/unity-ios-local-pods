using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace iDreams.IOSLocalPods
{
  /// <summary>
  /// iOS builds: enforces EDM's CocoaPods settings and, when local pods are missing (fresh checkout with the pods
  /// directory ignored, or a project not set up yet), asks to generate them right away: Generate / Cancel build /
  /// Generate + ignore the folder in version control. In batch mode it generates without asking. Then re-applies
  /// path= to *Dependencies.xml overwritten by a plugin update and raises installed podspecs to the current
  /// deployment target.
  /// </summary>
  internal class IOSLocalPodsBuildPreprocessor : IPreprocessBuildWithReport
  {
    private const string Title = "iOS Local Pods";

    // First, so a project preprocessor (e.g. an offline build) can still turn CocoaPods off afterwards.
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
      if (report.summary.platform != BuildTarget.iOS)
      {
        return;
      }

      IOSLocalPodsSettings settings = IOSLocalPodsSettings.Reload();
      if (settings._EnforceCocoaPodsSettings)
      {
        IOSLocalPods.ApplyCocoaPodsSettings();
      }
      if (settings._CheckOnBuild)
      {
        GenerateMissingPods(settings);
      }

      foreach (IOSLocalPodsSettings.cPod pod in settings.ValidPods.Where(p => IOSLocalPods.IsInstalled(settings, p._Name)))
      {
        if (IOSLocalPods.RaiseInstalledDeploymentTarget(settings, pod._Name))
        {
          Debug.Log("[IOSLocalPods] " + pod._Name + ": podspec deployment target raised to iOS " + settings.ResolvedDeploymentTarget);
        }
      }
      foreach (string file in IOSLocalPods.ApplyToDependencyXmls(settings))
      {
        Debug.LogWarning("[IOSLocalPods] Re-applied local pod paths to " + file + " (overwritten by a plugin update?). Check in the change.");
      }
      foreach (string name in IOSLocalPods.UndeclaredPods(settings))
      {
        Debug.LogWarning("[IOSLocalPods] " + name + " is configured but no *Dependencies.xml declares it; the local copy is not used.");
      }
    }

    private static void GenerateMissingPods(IOSLocalPodsSettings settings)
    {
      List<IOSLocalPodsSettings.cPod> pods = IOSLocalPods.MissingPods(settings);
      bool setUp = settings.ValidPods.Any();
      if (!setUp)
      {
        pods = IOSLocalPods.DetectRemotePods()
          .Select(p => new IOSLocalPodsSettings.cPod { _Name = p.Key, _Requirement = p.Value })
          .ToList();
      }
      if (pods.Count == 0)
      {
        return;
      }

      string list = string.Join("\n", pods.Select(p => "    " + p._Name + "  " + p._Requirement));
      string message = (setUp
          ? "Local CocoaPods are missing in " + settings._PodsDirectory + "/:\n\n" + list
          : "This project has no local CocoaPods yet. Pods declared in *Dependencies.xml:\n\n" + list
            + "\n\n(To keep pods remote, turn off \"Check on iOS Build\" in Project Settings > iOS Local Pods.)")
        + "\n\nThe iOS build needs them (Google ships ios 12.0 podspecs, Xcode 27 requires " + settings.ResolvedDeploymentTarget + "+). "
        + "Generate them now? They are downloaded from the CocoaPods CDN; Cancel stops the build.";

      VcsIgnore.cTarget ignore = VcsIgnore.Find(settings._PodsDirectory);
      bool canIgnore = ignore != null && !VcsIgnore.IsIgnored(ignore);
      int choice;
      if (Application.isBatchMode)
      {
        Debug.Log("[IOSLocalPods] Batch mode: generating missing local pods.\n" + list);
        choice = 0;
      }
      else if (canIgnore)
      {
        message += "\n\n\"Generate + Ignore Folder\" also adds " + ignore._Pattern + " to " + System.IO.Path.GetFileName(ignore._IgnoreFile)
          + " (" + ignore._Vcs + "), so every machine generates the pods on its first iOS build instead of checking them in.";
        choice = EditorUtility.DisplayDialogComplex(Title, message, "Generate", "Cancel Build", "Generate + Ignore Folder");
      }
      else
      {
        choice = EditorUtility.DisplayDialog(Title, message, "Generate", "Cancel Build") ? 0 : 1;
      }

      if (choice == 1)
      {
        throw new BuildFailedException("[IOSLocalPods] Build cancelled: local pods are missing (" + string.Join(", ", pods.Select(p => p._Name))
          + "). Generate them with Assets > External Dependency Manager > iOS Resolver > Refresh Local Pods.");
      }
      if (choice == 2)
      {
        Debug.Log("[IOSLocalPods] " + VcsIgnore.Add(ignore));
      }
      if (!setUp)
      {
        settings._Pods.AddRange(pods);
        settings.Save();
      }

      IOSLocalPods.cRefreshResult result;
      try
      {
        result = IOSLocalPods.RefreshPods(settings, pods);
      }
      catch (OperationCanceledException)
      {
        throw new BuildFailedException("[IOSLocalPods] Build cancelled while generating local pods.");
      }
      IOSLocalPods.LogResult(result);
      if (result._Failed.Count > 0)
      {
        throw new BuildFailedException("[IOSLocalPods] Could not generate local pods: " + string.Join(", ", result._Failed) + ". See the Console.");
      }
    }
  }
}
