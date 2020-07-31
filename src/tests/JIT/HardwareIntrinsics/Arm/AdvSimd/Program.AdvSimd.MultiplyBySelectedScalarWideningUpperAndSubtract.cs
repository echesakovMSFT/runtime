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
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.Int16.Vector64.Int16.3"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_Int16_Vector64_Int16_3,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.Int16.Vector128.Int16.7"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_Int16_Vector128_Int16_7,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.Int32.Vector64.Int32.1"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_Int32_Vector64_Int32_1,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.Int32.Vector128.Int32.3"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_Int32_Vector128_Int32_3,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.UInt16.Vector64.UInt16.3"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_UInt16_Vector64_UInt16_3,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.UInt16.Vector128.UInt16.7"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_UInt16_Vector128_UInt16_7,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.UInt32.Vector64.UInt32.1"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_UInt32_Vector64_UInt32_1,
                ["MultiplyBySelectedScalarWideningUpperAndSubtract.Vector128.UInt32.Vector128.UInt32.3"] = MultiplyBySelectedScalarWideningUpperAndSubtract_Vector128_UInt32_Vector128_UInt32_3,
            };
        }
    }
}
