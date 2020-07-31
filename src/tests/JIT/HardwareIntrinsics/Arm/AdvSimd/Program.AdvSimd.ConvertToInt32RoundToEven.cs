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
                ["ConvertToInt32RoundToEven.Vector64.Single"] = ConvertToInt32RoundToEven_Vector64_Single,
                ["ConvertToInt32RoundToEven.Vector128.Single"] = ConvertToInt32RoundToEven_Vector128_Single,
            };
        }
    }
}
