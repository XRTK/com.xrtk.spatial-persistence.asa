// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using XRTK.Editor.Utilities;

namespace XRTK.SpatialPersistence.ASA.Editor
{
    /// <summary>
    /// Dummy scriptable object used to find the relative path of the com.xrtk.spatial-persistence.asa.
    /// </summary>
    ///// <inheritdoc cref="IPathFinder" />
    public class AnchorsASAPathFinder : ScriptableObject, IPathFinder
    {
        ///// <inheritdoc />
        public string Location => $"/Editor/{nameof(AnchorsASAPathFinder)}.cs";
    }
}
