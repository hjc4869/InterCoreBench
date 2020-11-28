using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InterCoreBench.Linux
{
    [SupportedOSPlatform("linux")]
    public unsafe class LinuxThreadAffinity : IThreadAffinity
    {
        [DllImport("libc", SetLastError=true)]
        static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ulong* mask);
        
        [DllImport("libc", SetLastError=true)]
        static extern int sched_getaffinity(int pid, IntPtr cpusetsize, ulong* mask);

        public void ResetAffinity(object context)
        {
            var mask = (ulong)context;
            if (sched_setaffinity(0, (IntPtr)(sizeof(ulong)), &mask) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "sched_setaffinity failed");
            }
        }

        public void SetAffinity(int core, out object context)
        {
            if (core >= 64)
            {
                throw new NotSupportedException("You're too rich to use this program.");
            }

            ulong existingMask;
            if (sched_getaffinity(0, (IntPtr)sizeof(ulong), &existingMask) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "sched_getaffinity failed");
            }

            context = existingMask;
            var mask = 1UL << core;
            if (sched_setaffinity(0, (IntPtr)(sizeof(ulong)), &mask) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "sched_setaffinity failed");
            }
        }
    }
}