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
                ["ShiftLeftAndInsertScalar.Vector64.Int64"] = ShiftLeftAndInsertScalar_Vector64_Int64,
                ["ShiftLeftAndInsertScalar.Vector64.UInt64"] = ShiftLeftAndInsertScalar_Vector64_UInt64,
            };
        }
    }
}
