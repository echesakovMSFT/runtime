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
                ["ShiftRightArithmeticRoundedNarrowingSaturateScalar.Vector64.Int16.32"] = ShiftRightArithmeticRoundedNarrowingSaturateScalar_Vector64_Int16_32,
                ["ShiftRightArithmeticRoundedNarrowingSaturateScalar.Vector64.Int32.64"] = ShiftRightArithmeticRoundedNarrowingSaturateScalar_Vector64_Int32_64,
                ["ShiftRightArithmeticRoundedNarrowingSaturateScalar.Vector64.SByte.16"] = ShiftRightArithmeticRoundedNarrowingSaturateScalar_Vector64_SByte_16,
            };
        }
    }
}
