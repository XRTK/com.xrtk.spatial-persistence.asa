// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.IO;
using UnityEditor;
using XRTK.Editor;
using XRTK.Editor.Utilities;
using XRTK.Extensions;

namespace XRTK.Anchors.ASA.Editor
{
    [InitializeOnLoad]
    internal static class Anchors.ASAPackageInstaller
    {
        private static readonly string DefaultPath = $"{MixedRealityPreferences.ProfileGenerationPath}Anchors.ASA";
        private static readonly string HiddenPath = Path.GetFullPath($"{PathFinderUtility.ResolvePath<IPathFinder>(typeof(Anchors.ASAPathFinder)).ToForwardSlashes()}\\{MixedRealityPreferences.HIDDEN_PROFILES_PATH}");

        static Anchors.ASAPackageInstaller()
        {
            if (!EditorPreferences.Get($"{nameof(Anchors.ASAPackageInstaller)}", false))
            {
                EditorPreferences.Set($"{nameof(Anchors.ASAPackageInstaller)}", PackageInstaller.TryInstallAssets(HiddenPath, DefaultPath));
            }
        }
    }
}
