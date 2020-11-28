using System;
using System.Collections.Generic;

namespace InterCoreBench
{
    interface ILogicalCoreInfo
    {
        List<int> GetPhysicalCoreIndex();
    }
}