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
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector64.Int16.Vector64.Int16.3"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector64_Int16_Vector64_Int16_3,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector64.Int16.Vector128.Int16.7"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector64_Int16_Vector128_Int16_7,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector64.Int32.Vector64.Int32.1"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector64_Int32_Vector64_Int32_1,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector64.Int32.Vector128.Int32.3"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector64_Int32_Vector128_Int32_3,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector128.Int16.Vector64.Int16.2"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector128_Int16_Vector64_Int16_2,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector128.Int16.Vector128.Int16.7"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector128_Int16_Vector128_Int16_7,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector128.Int32.Vector64.Int32.1"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector128_Int32_Vector64_Int32_1,
                ["MultiplyRoundedDoublingBySelectedScalarSaturateHigh.Vector128.Int32.Vector128.Int32.3"] = MultiplyRoundedDoublingBySelectedScalarSaturateHigh_Vector128_Int32_Vector128_Int32_3,
            };
        }
    }
}
