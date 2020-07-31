// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace JIT.HardwareIntrinsics.Arm
{
    public static partial class Program
    {
        static Program()
        {
            TestList = new Dictionary<string, Action>()
            {
                ["ConvertToUInt64RoundToNegativeInfinity.Vector128.Double"] = ConvertToUInt64RoundToNegativeInfinity_Vector128_Double,
            };
        }
    }
}
