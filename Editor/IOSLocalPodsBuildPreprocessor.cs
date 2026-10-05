using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace iDreams.IOSLocalPods
{
  /// <summary>
  /// iOS builds: enforces EDM's CocoaPods settings, fails early when a configured local pod is missing,
  /// re-applies path= to *Dependencies.xml overwritten by a plugin update, and raises installed podspecs
  /// to the current deployment target. No network access.
  /// </summary>
  internal class IOSLocalPodsBuildPreprocessor : IPreprocessBuildWithReport
  {
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
      if (!settings.ValidPods.Any())
      {
        return;
      }

      string[] missing = settings.ValidPods.Select(p => p._Name).Where(n => !File.Exists(settings.PodspecPath(n))).ToArray();
      if (missing.Length > 0)
      {
        throw new BuildFailedException("[IOSLocalPods] Missing local pods in " + settings._PodsDirectory + ": " + string.Join(", ", missing)
          + ". Run Assets > External Dependency Manager > iOS Resolver > Refresh Local Pods.");
      }

      foreach (IOSLocalPodsSettings.cPod pod in settings.ValidPods)
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
  }
}
