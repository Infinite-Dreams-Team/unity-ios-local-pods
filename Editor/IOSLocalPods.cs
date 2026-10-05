using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;
using Formatting = Newtonsoft.Json.Formatting;

namespace iDreams.IOSLocalPods
{
  /// <summary>
  /// Serves CocoaPods from a project folder instead of the public specs. CocoaPods takes each pod
  /// target's IPHONEOS_DEPLOYMENT_TARGET from the pod's podspec, not from the Podfile's platform, and
  /// Google ships its binary pods with ios 12.0, which Xcode 27 rejects (15.0+). Refresh Local Pods
  /// downloads the newest version allowed by the configured requirement from the CocoaPods CDN,
  /// stores it with the podspec's deployment target raised, and points the pod's *Dependencies.xml
  /// entries at the folder (path=), which EDM writes into the Podfile as :path =>.
  /// </summary>
  public static class IOSLocalPods
  {
    private const string MenuRoot = "Assets/External Dependency Manager/iOS Resolver/";
    private const string TempDir = "Temp/IOSLocalPods";
    private const string CdnUrl = "https://cdn.cocoapods.org/";
    private const string Title = "Refresh Local Pods";

    [MenuItem(MenuRoot + "Refresh Local Pods")]
    public static void RefreshLocalPods()
    {
      IOSLocalPodsSettings settings = IOSLocalPodsSettings.Reload();
      if (!settings.ValidPods.Any() && !OfferDetectedPods(settings))
      {
        return;
      }

      StringBuilder report = new StringBuilder();
      bool failed = false;
      try
      {
        AdoptRequirementsFromXml(settings);
        foreach (IOSLocalPodsSettings.cPod pod in settings.ValidPods.ToList())
        {
          try
          {
            report.AppendLine(RefreshPod(settings, pod));
          }
          catch (OperationCanceledException)
          {
            throw;
          }
          catch (Exception e)
          {
            failed = true;
            report.AppendLine(pod._Name + ": FAILED - " + e.Message);
            Debug.LogException(e);
          }
        }

        foreach (string file in ApplyToDependencyXmls(settings))
        {
          report.AppendLine("Switched to local pods: " + file);
        }
        foreach (string name in UndeclaredPods(settings))
        {
          report.AppendLine(name + ": not declared by any *Dependencies.xml, so it is not used");
        }
        if (settings._EnforceCocoaPodsSettings)
        {
          ApplyCocoaPodsSettings();
        }

        if (failed)
        {
          Debug.LogError("[IOSLocalPods] Refresh finished with errors:\n" + report);
        }
        else
        {
          Debug.Log("[IOSLocalPods] Local pods refreshed:\n" + report);
        }
        EditorUtility.DisplayDialog(Title, report.ToString(), "OK");
      }
      catch (OperationCanceledException)
      {
        Debug.LogWarning("[IOSLocalPods] Cancelled.\n" + report);
      }
      finally
      {
        EditorUtility.ClearProgressBar();
        DeleteDirectory(TempDir);
      }
    }

    [MenuItem(MenuRoot + "Local Pods Settings")]
    private static void OpenSettings()
    {
      SettingsService.OpenProjectSettings("Project/iOS Local Pods");
    }

    // ---------- Dependencies.xml ----------

    /// <summary>The *Dependencies.xml files EDM reads: Editor folders under Assets and embedded packages.</summary>
    public static IEnumerable<string> DependencyXmlFiles()
    {
      List<string> roots = new List<string> { "Assets" };
      if (Directory.Exists("Packages"))
      {
        roots.AddRange(Directory.GetDirectories("Packages"));
      }
      return roots
        .SelectMany(r => Directory.GetFiles(r, "*Dependencies.xml", SearchOption.AllDirectories))
        .Select(f => f.Replace('\\', '/'))
        .Where(f => f.Contains("/Editor/"))
        .OrderBy(f => f);
    }

    private static IEnumerable<XmlElement> IosPods(XmlDocument document)
    {
      return document.SelectNodes("/dependencies/iosPods/iosPod").Cast<XmlElement>();
    }

    private static XmlDocument LoadXml(string file)
    {
      XmlDocument document = new XmlDocument { PreserveWhitespace = true };
      document.Load(file);
      return document;
    }

    /// <summary>Remote pods (declared with a version, not a path) found in *Dependencies.xml: name -> requirement.</summary>
    public static Dictionary<string, string> DetectRemotePods()
    {
      Dictionary<string, string> pods = new Dictionary<string, string>();
      foreach (string file in DependencyXmlFiles())
      {
        try
        {
          foreach (XmlElement pod in IosPods(LoadXml(file)))
          {
            string name = pod.GetAttribute("name");
            if (!string.IsNullOrEmpty(name) && !pod.HasAttribute("path") && !pods.ContainsKey(name))
            {
              pods[name] = pod.GetAttribute("version");
            }
          }
        }
        catch (XmlException e)
        {
          Debug.LogWarning("[IOSLocalPods] Skipping " + file + ": " + e.Message);
        }
      }
      return pods;
    }

    public static int AddDetectedPods(IOSLocalPodsSettings settings)
    {
      HashSet<string> known = new HashSet<string>(settings.ValidPods.Select(p => p._Name));
      List<KeyValuePair<string, string>> found = DetectRemotePods().Where(p => !known.Contains(p.Key)).ToList();
      foreach (KeyValuePair<string, string> pod in found)
      {
        settings._Pods.Add(new IOSLocalPodsSettings.cPod { _Name = pod.Key, _Requirement = pod.Value });
      }
      EditorUtility.DisplayDialog("Detect Pods", found.Count == 0
        ? "No new remote pods found in *Dependencies.xml."
        : "Added:\n" + string.Join("\n", found.Select(p => p.Key + " " + p.Value)) + "\n\nRemove the ones that should stay remote, then Refresh Local Pods.", "OK");
      return found.Count;
    }

    private static bool OfferDetectedPods(IOSLocalPodsSettings settings)
    {
      Dictionary<string, string> found = DetectRemotePods();
      if (found.Count == 0)
      {
        EditorUtility.DisplayDialog(Title, "No pods configured and no remote pods found in *Dependencies.xml.", "OK");
        return false;
      }
      int choice = EditorUtility.DisplayDialogComplex(Title,
        "No pods configured yet. Found in *Dependencies.xml:\n\n" + string.Join("\n", found.Select(p => p.Key + " " + p.Value))
        + "\n\nServe all of them locally? Pods with remote dependencies keep those dependencies' deployment targets.",
        "Use All", "Cancel", "Open Settings");
      if (choice == 2)
      {
        OpenSettings();
      }
      if (choice != 0)
      {
        return false;
      }
      foreach (KeyValuePair<string, string> pod in found)
      {
        settings._Pods.Add(new IOSLocalPodsSettings.cPod { _Name = pod.Key, _Requirement = pod.Value });
      }
      settings.Save();
      return true;
    }

    /// <summary>A pod without a requirement takes the version its plugin still declares in the XML.</summary>
    private static void AdoptRequirementsFromXml(IOSLocalPodsSettings settings)
    {
      Dictionary<string, string> remote = DetectRemotePods();
      bool changed = false;
      foreach (IOSLocalPodsSettings.cPod pod in settings.ValidPods)
      {
        string version;
        if (string.IsNullOrEmpty(pod._Requirement) && remote.TryGetValue(pod._Name, out version) && !string.IsNullOrEmpty(version))
        {
          pod._Requirement = version;
          changed = true;
        }
      }
      if (changed)
      {
        settings.Save();
      }
    }

    /// <summary>
    /// Points every configured pod's iosPod entry at its local folder: sets path=, drops version= (CocoaPods
    /// rejects a version on a :path pod) and <sources>. Returns the files changed. Plugin updates overwrite
    /// these XMLs; the build preprocessor calls this again.
    /// </summary>
    public static List<string> ApplyToDependencyXmls(IOSLocalPodsSettings settings)
    {
      Dictionary<string, string> paths = settings.ValidPods.ToDictionary(p => p._Name, p => settings.PodDirectory(p._Name));
      List<string> changedFiles = new List<string>();
      foreach (string file in DependencyXmlFiles())
      {
        XmlDocument document;
        try
        {
          document = LoadXml(file);
        }
        catch (XmlException)
        {
          continue;
        }

        bool changed = false;
        foreach (XmlElement pod in IosPods(document))
        {
          string path;
          if (!paths.TryGetValue(pod.GetAttribute("name"), out path))
          {
            continue;
          }
          if (pod.HasAttribute("version"))
          {
            pod.RemoveAttribute("version");
            changed = true;
          }
          if (pod.GetAttribute("path") != path)
          {
            pod.SetAttribute("path", path);
            changed = true;
          }
          foreach (XmlNode sources in pod.SelectNodes("sources").Cast<XmlNode>().ToList())
          {
            pod.RemoveChild(sources);
            changed = true;
          }
          if (!pod.HasChildNodes || pod.ChildNodes.Cast<XmlNode>().All(n => n.NodeType == XmlNodeType.Whitespace))
          {
            while (pod.HasChildNodes)
            {
              pod.RemoveChild(pod.FirstChild);
            }
            pod.IsEmpty = true;
          }
        }

        if (changed)
        {
          SaveXml(document, file);
          changedFiles.Add(file);
        }
      }
      return changedFiles;
    }

    /// <summary>Configured pods no *Dependencies.xml declares (local folder unused).</summary>
    public static List<string> UndeclaredPods(IOSLocalPodsSettings settings)
    {
      HashSet<string> declared = new HashSet<string>();
      foreach (string file in DependencyXmlFiles())
      {
        try
        {
          declared.UnionWith(IosPods(LoadXml(file)).Select(p => p.GetAttribute("name")));
        }
        catch (XmlException)
        {
        }
      }
      return settings.ValidPods.Select(p => p._Name).Where(n => !declared.Contains(n)).ToList();
    }

    private static void SaveXml(XmlDocument document, string file)
    {
      byte[] raw = File.ReadAllBytes(file);
      bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
      XmlWriterSettings writerSettings = new XmlWriterSettings
      {
        Encoding = new UTF8Encoding(bom),
        OmitXmlDeclaration = !(document.FirstChild is XmlDeclaration),
      };
      using (XmlWriter writer = XmlWriter.Create(file, writerSettings))
      {
        document.Save(writer);
      }
    }

    // ---------- Pods ----------

    private static string RefreshPod(IOSLocalPodsSettings settings, IOSLocalPodsSettings.cPod pod)
    {
      string name = pod._Name.Trim();
      string requirement = pod._Requirement ?? "";
      string shard = Shard(name);

      string versions = Encoding.UTF8.GetString(Download(CdnUrl + "all_pods_versions_" + shard.Replace('/', '_') + ".txt", name));
      string line = versions.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(name + "/"));
      if (line == null)
      {
        throw new Exception("not found on the CocoaPods CDN");
      }
      string version = line.Split('/').Skip(1)
        .Where(v => ParseVersion(v) != null && Matches(ParseVersion(v), requirement))
        .OrderBy(ParseVersion)
        .LastOrDefault();
      if (version == null)
      {
        throw new Exception("no published version matches '" + requirement + "'");
      }

      JObject spec = JObject.Parse(Encoding.UTF8.GetString(Download(CdnUrl + "Specs/" + shard + "/" + name + "/" + version + "/" + name + ".podspec.json", name)));
      string archiveUrl = (string)spec["source"]?["http"];
      if (string.IsNullOrEmpty(archiveUrl))
      {
        throw new Exception(version + " has no http source (git/other sources are not supported)");
      }

      string work = Path.Combine(TempDir, name);
      string archive = Path.Combine(TempDir, name + ".archive");
      DeleteDirectory(work);
      Directory.CreateDirectory(work);
      DownloadFile(archiveUrl, archive, name + " " + version);
      Extract(archive, work);
      MakeWritable(work);

      string deploymentTarget = RaiseDeploymentTarget(spec, settings.ResolvedDeploymentTarget);
      File.WriteAllText(Path.Combine(work, name + ".podspec.json"), spec.ToString(Formatting.Indented) + "\n");

      string destination = settings.PodDirectory(name);
      string previousVersion = InstalledVersion(name);
      DeleteDirectory(destination);
      Directory.CreateDirectory(settings._PodsDirectory);
      Directory.Move(work, destination);

      string result = name + ": " + (previousVersion ?? "-") + " -> " + version + " (iOS " + deploymentTarget + ")";
      HashSet<string> local = new HashSet<string>(settings.ValidPods.Select(p => p._Name));
      List<string> remoteDependencies = (spec["dependencies"] as JObject)?.Properties()
        .Select(p => p.Name.Split('/')[0]).Distinct().Where(d => !local.Contains(d)).ToList();
      if (remoteDependencies != null && remoteDependencies.Count > 0)
      {
        result += "; remote dependencies keep their own deployment target: " + string.Join(", ", remoteDependencies);
      }
      return result;
    }

    public static string InstalledVersion(string name)
    {
      if (string.IsNullOrEmpty(name))
      {
        return null;
      }
      string podspecPath = IOSLocalPodsSettings.Instance.PodspecPath(name);
      if (!File.Exists(podspecPath))
      {
        return null;
      }
      try
      {
        return (string)JObject.Parse(File.ReadAllText(podspecPath))["version"];
      }
      catch (JsonException)
      {
        return null;
      }
    }

    /// <summary>Raises an installed podspec's deployment target in place (no download). True if the file changed.</summary>
    public static bool RaiseInstalledDeploymentTarget(IOSLocalPodsSettings settings, string name)
    {
      string podspecPath = settings.PodspecPath(name);
      JObject spec = JObject.Parse(File.ReadAllText(podspecPath));
      string before = (string)spec["platforms"]?["ios"];
      if (RaiseDeploymentTarget(spec, settings.ResolvedDeploymentTarget) == before)
      {
        return false;
      }
      File.WriteAllText(podspecPath, spec.ToString(Formatting.Indented) + "\n");
      return true;
    }

    private static string RaiseDeploymentTarget(JObject spec, string minimum)
    {
      JObject platforms = spec["platforms"] as JObject;
      if (platforms == null)
      {
        platforms = new JObject();
        spec["platforms"] = platforms;
      }
      Version current = ParseVersion((string)platforms["ios"]);
      if (current == null || current < ParseVersion(minimum))
      {
        platforms["ios"] = minimum;
      }
      return (string)platforms["ios"];
    }

    /// <summary>CocoaPods CDN shards pods by the first three hex digits of the MD5 of the pod name.</summary>
    private static string Shard(string name)
    {
      using (MD5 md5 = MD5.Create())
      {
        string hash = string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(name)).Select(b => b.ToString("x2")));
        return hash[0] + "/" + hash[1] + "/" + hash[2];
      }
    }

    /// <summary>Release versions only ("13.11.0"); pre-releases ("13.0.0-beta") return null.</summary>
    private static Version ParseVersion(string text)
    {
      Version version;
      if (string.IsNullOrEmpty(text))
      {
        return null;
      }
      text = text.Trim();
      // System.Version needs at least "major.minor".
      if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out version))
      {
        return null;
      }
      return new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
    }

    /// <summary>CocoaPods requirement syntax: "1.2.3", "= 1.2.3", ">= 1.2", "< 2", "~> 13.9", comma separated; empty matches all.</summary>
    private static bool Matches(Version version, string requirement)
    {
      foreach (string clause in requirement.Split(',').Where(c => c.Trim().Length > 0))
      {
        string text = clause.Trim();
        string op = new[] { "~>", ">=", "<=", ">", "<", "=" }.FirstOrDefault(o => text.StartsWith(o)) ?? "=";
        if (text.StartsWith(op))
        {
          text = text.Substring(op.Length).Trim();
        }
        Version required = ParseVersion(text);
        if (required == null)
        {
          throw new Exception("cannot parse requirement '" + requirement + "'");
        }

        bool matches;
        switch (op)
        {
          case "~>":
            // "~> 13" and "~> 13.9" mean < 14.0, "~> 13.9.1" means < 13.10.
            Version upper = text.Split('.').Length <= 2 ? new Version(required.Major + 1, 0, 0)
                                                        : new Version(required.Major, required.Minor + 1, 0);
            matches = version >= required && version < upper;
            break;
          case ">=": matches = version >= required; break;
          case "<=": matches = version <= required; break;
          case ">": matches = version > required; break;
          case "<": matches = version < required; break;
          default: matches = version == required; break;
        }
        if (!matches)
        {
          return false;
        }
      }
      return true;
    }

    // ---------- EDM CocoaPods settings ----------

    /// <summary>
    /// Turns on EDM's Podfile generation, Workspace integration and pod install (EDM persists these in
    /// ProjectSettings/GvhProjectSettings.xml). Without them EDM writes no Podfile and the pods' symbols are
    /// missing at link time. Reflection keeps this package compiling in projects without EDM.
    /// </summary>
    public static bool ApplyCocoaPodsSettings()
    {
      Type resolver = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("Google.IOSResolver", false))
        .FirstOrDefault(t => t != null);
      if (resolver == null)
      {
        Debug.LogWarning("[IOSLocalPods] External Dependency Manager (Google.IOSResolver) not found; CocoaPods settings not applied.");
        return false;
      }

      bool ok = SetStatic(resolver, "PodfileGenerationEnabled", true);
      ok &= SetStatic(resolver, "PodToolExecutionViaShellEnabled", true);
      ok &= SetStatic(resolver, "AutoPodToolInstallInEditorEnabled", true);
      Type method = resolver.GetNestedType("CocoapodsIntegrationMethod");
      ok &= method != null && SetStatic(resolver, "CocoapodsIntegrationMethodPref", Enum.Parse(method, "Workspace"));
      Debug.Log("[IOSLocalPods] EDM CocoaPods settings: Podfile generation ON, integration = Workspace, pod install via shell ON" + (ok ? "" : " (some settings could not be set, see warnings)"));
      return ok;
    }

    private static bool SetStatic(Type type, string property, object value)
    {
      PropertyInfo info = type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
      if (info == null || !info.CanWrite)
      {
        Debug.LogWarning("[IOSLocalPods] " + type.FullName + "." + property + " not found in this EDM version.");
        return false;
      }
      info.SetValue(null, value);
      return true;
    }

    // ---------- IO ----------

    private static byte[] Download(string url, string label)
    {
      using (UnityWebRequest request = UnityWebRequest.Get(url))
      {
        Send(request, url, label);
        return request.downloadHandler.data;
      }
    }

    private static void DownloadFile(string url, string path, string label)
    {
      using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(Path.GetFullPath(path)), null))
      {
        Send(request, url, label);
      }
    }

    private static void Send(UnityWebRequest request, string url, string label)
    {
      UnityWebRequestAsyncOperation operation = request.SendWebRequest();
      while (!operation.isDone)
      {
        if (EditorUtility.DisplayCancelableProgressBar(Title, label + "\n" + url, request.downloadProgress))
        {
          request.Abort();
          throw new OperationCanceledException();
        }
        Thread.Sleep(50);
      }
      if (request.result != UnityWebRequest.Result.Success)
      {
        throw new Exception(url + ": " + request.error);
      }
    }

    /// <summary>bsdtar (macOS, Windows 10+) detects tar.gz / zip by content.</summary>
    private static void Extract(string archive, string destination)
    {
      ProcessStartInfo info = new ProcessStartInfo("tar", "-xf \"" + Path.GetFullPath(archive) + "\" -C \"" + Path.GetFullPath(destination) + "\"")
      {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true,
      };
      using (Process process = Process.Start(info))
      {
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
          throw new Exception("tar failed for " + archive + ": " + error);
        }
      }
    }

    /// <summary>Google ships some files read-only; version control and Directory.Delete need them writable.</summary>
    private static void MakeWritable(string directory)
    {
      if (!Directory.Exists(directory))
      {
        return;
      }
      foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
      {
        File.SetAttributes(file, FileAttributes.Normal);
      }
    }

    private static void DeleteDirectory(string directory)
    {
      if (Directory.Exists(directory))
      {
        MakeWritable(directory);
        Directory.Delete(directory, true);
      }
    }
  }
}
