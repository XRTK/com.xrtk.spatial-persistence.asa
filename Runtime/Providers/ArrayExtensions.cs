using System;

namespace XRTK.Providers.SpatialPersistence
{
    public static class ArrayExtensions
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
