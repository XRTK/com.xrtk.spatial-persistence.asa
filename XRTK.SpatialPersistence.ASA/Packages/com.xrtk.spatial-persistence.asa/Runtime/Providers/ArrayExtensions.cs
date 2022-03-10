// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace XRTK.Providers.SpatialPersistence
{
    internal static class ArrayExtensions
    {
        public static string[] ToStringArray(this Guid[] input)
        {
            var newArray = new string[input.Length];

            for (var i = 0; i < input.Length; i++)
            {
                newArray[i] = input[i].ToString();
            }

            return newArray;
        }
    }
}
