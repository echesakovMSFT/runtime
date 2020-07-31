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
                ["FusedMultiplyAddScalarBySelectedScalar.Vector64.Double.Vector128.Double.1"] = FusedMultiplyAddScalarBySelectedScalar_Vector64_Double_Vector128_Double_1,
                ["FusedMultiplyAddScalarBySelectedScalar.Vector64.Single.Vector64.Single.1"] = FusedMultiplyAddScalarBySelectedScalar_Vector64_Single_Vector64_Single_1,
                ["FusedMultiplyAddScalarBySelectedScalar.Vector64.Single.Vector128.Single.3"] = FusedMultiplyAddScalarBySelectedScalar_Vector64_Single_Vector128_Single_3,
            };
        }
    }
}
