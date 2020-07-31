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
                ["ShiftArithmeticRoundedSaturateScalar.Vector64.Int16"] = ShiftArithmeticRoundedSaturateScalar_Vector64_Int16,
                ["ShiftArithmeticRoundedSaturateScalar.Vector64.Int32"] = ShiftArithmeticRoundedSaturateScalar_Vector64_Int32,
                ["ShiftArithmeticRoundedSaturateScalar.Vector64.SByte"] = ShiftArithmeticRoundedSaturateScalar_Vector64_SByte,
            };
        }
    }
}
