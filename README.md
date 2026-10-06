# iOS Local Pods (`pl.idreams.ios-local-pods`)

Fixes iOS builds where CocoaPods targets fail in Xcode 27 with

> The iOS deployment target 'IPHONEOS_DEPLOYMENT_TARGET' is set to 12.0, but the range of
> supported deployment target versions is 15.0 to 27.0.x.

and builds where External Dependency Manager (EDM4U) silently stops generating a Podfile
(missing symbols for Google Mobile Ads etc. at link time). No Podfile `post_install` patching.

## How it works

CocoaPods sets each pod target's deployment target from the pod's **podspec**, not from the
Podfile's `platform`. Google publishes its binary pods (Google-Mobile-Ads-SDK,
GoogleUserMessagingPlatform, …) with `ios: 12.0`. The package:

1. Downloads the pod from the CocoaPods CDN into a project folder (`iOSPods/<Pod>/`, outside
   `Assets`), unmodified, with the podspec's `platforms.ios` raised to the project's iOS target.
2. Switches the pod's entry in `*Dependencies.xml` to `path="iOSPods/<Pod>"`. EDM writes that into
   the Podfile as `pod '<Pod>', :path => '<absolute path>'`.
3. On every iOS build (preprocessor, `callbackOrder 0`):
   - turns on EDM's Podfile generation, Workspace integration and `pod install` (optional),
   - if local pods are missing (fresh checkout with the pods folder ignored, or a project not set
     up yet), asks: **Generate** (download now and continue), **Cancel Build**, or
     **Generate + Ignore Folder** (also adds the folder to `ignore.conf` / `.gitignore`). Batch-mode
     builds generate without asking,
   - re-applies `path=` if a plugin update overwrote a `*Dependencies.xml` (logs a warning),
   - raises the stored podspecs if the Player Settings iOS target went up.

Pods that are not installed (failed download) are never switched to `path=`; they stay remote.

## Install

Add to `Packages/manifest.json` (private repo of Infinite-Dreams-Team):

```json
"pl.idreams.ios-local-pods": "https://github.com/Infinite-Dreams-Team/unity-ios-local-pods.git#v1.1.0"
```

Unity clones it with the system `git`, so every machine needs read access to the repo: run
`gh auth login` and `gh auth setup-git` once (HTTPS), or use an SSH key / read-only deploy key with
`git@github.com:Infinite-Dreams-Team/unity-ios-local-pods.git#v1.1.0`. Update by pointing `#` at a
newer tag.

Without GitHub access: `npm pack` this folder, copy the `.tgz` into the project's `Packages/`,
check it in and reference it as `"file:pl.idreams.ios-local-pods-1.1.0.tgz"`.

## Use

**Assets > External Dependency Manager > iOS Resolver > Refresh Local Pods** — the iOS
counterpart of the Android Resolver's Force Resolve. On the first run with nothing configured it
offers the pods found in `*Dependencies.xml`. It downloads the newest version allowed by each
pod's requirement, replaces `iOSPods/<Pod>/` and updates the XMLs. Check in
`ProjectSettings/IOSLocalPodsSettings.json`, the changed XMLs and either `iOSPods/` or the ignore
entry for it.

### Check in the pods or ignore them?

The pods are ~30 MB for Google Mobile Ads + UMP, more with mediation adapters. Ignoring the folder
keeps them out of the repository: every machine (build Mac included) gets the Generate dialog on its
first iOS build and downloads them. Checking them in makes builds work offline and pins the exact
binaries. Ignore with the dialog button or **Project Settings > iOS Local Pods > Version Control**;
if the folder is already checked in, remove it from version control while keeping the local files.

**Project Settings > iOS Local Pods** (also **… > iOS Resolver > Local Pods Settings**):

| Setting | |
|---|---|
| Pods Directory | Default `iOSPods`, relative to the project root. |
| iOS Deployment Target | Empty = Player Settings target. |
| Enforce CocoaPods Settings | Force EDM's CocoaPods integration on for iOS builds. Runs first, so a project preprocessor (e.g. an offline build) can still turn it off. |
| Check on iOS Build | Offer to generate missing local pods when an iOS build starts. Turn off to keep pods remote without being asked. |
| Version Control | Shows whether the pods folder is ignored in Plastic SCM (`ignore.conf`) or git (`.gitignore`), with a button to add it. |
| Pods | Name + CocoaPods requirement (`~> 13.9`, `3.1.0`, `>= 2.0, < 3`; empty = newest). **Detect from Dependencies.xml** adds the remote pods declared there. |

## Limits

- Only pods with an `http` source (Google's binary pods). Pods with other sources are removed from
  the list and stay remote.
- A local pod's own dependencies stay remote with their own deployment targets; Refresh reports
  them. Localise them too, or drop the pod if the platform doesn't use it (e.g. GoogleSignIn on
  iOS pulls AppAuth / GTMAppAuth / GTMSessionFetcher with iOS 9 targets).
- Extraction uses `tar` (macOS, Windows 10+).
- Open `Unity-iPhone.xcworkspace` in Xcode, not the `.xcodeproj`.
