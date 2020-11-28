using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace InterCoreBench.Windows
{
    [SupportedOSPlatform("windows")]
    public class WindowsThreadAffinity : IThreadAffinity
    {
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        public void ResetAffinity(object context)
        {
            Thread.EndThreadAffinity();
        }

        public void SetAffinity(int core, out object context)
        {
            context = null;
            var mask = 1L << core;
            var process = Process.GetCurrentProcess();
            var threadId = GetCurrentThreadId();
            bool set = false;
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.Id == threadId)
                {
                    thread.ProcessorAffinity = (IntPtr)mask;
                    set = true;
                }
            }

            if (!set)
            {
                throw new Exception("Failed to set affinity");
            }

            Thread.BeginThreadAffinity();
        }
    }
}