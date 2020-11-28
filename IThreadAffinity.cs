using System;

namespace InterCoreBench
{
    interface IThreadAffinity
    {
        void SetAffinity(int core, out object context);
        void ResetAffinity(object context);
    }
}