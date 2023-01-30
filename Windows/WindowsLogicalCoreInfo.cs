using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InterCoreBench.Windows
{
    [SupportedOSPlatform("windows")]
    public class WindowsLogicalCoreInfo : ILogicalCoreInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSORCORE
        {
            public byte Flags;
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct NUMANODE
        {
            public uint NodeNumber;
        }

        private enum PROCESSOR_CACHE_TYPE
        {
            CacheUnified,
            CacheInstruction,
            CacheData,
            CacheTrace
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CACHE_DESCRIPTOR
        {
            public byte Level;
            public byte Associativity;
            public ushort LineSize;
            public uint Size;
            public PROCESSOR_CACHE_TYPE Type;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_UNION
        {
            [FieldOffset(0)]
            public PROCESSORCORE ProcessorCore;
            [FieldOffset(0)]
            public NUMANODE NumaNode;
            [FieldOffset(0)]
            public CACHE_DESCRIPTOR Cache;
            [FieldOffset(0)]
            private UInt64 Reserved1;
            [FieldOffset(8)]
            private UInt64 Reserved2;
        }

        private enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore,
            RelationNumaNode,
            RelationCache,
            RelationProcessorPackage,
            RelationGroup,
            RelationAll = 0xffff
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask;
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public SYSTEM_LOGICAL_PROCESSOR_INFORMATION_UNION ProcessorInformation;
        }

        [DllImport(@"kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(
            IntPtr Buffer,
            ref uint ReturnLength
        );

        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        private static SYSTEM_LOGICAL_PROCESSOR_INFORMATION[] GetLogicalProcessorInformation()
        {
            uint ReturnLength = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref ReturnLength);
            if (Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
            {
                IntPtr Ptr = Marshal.AllocHGlobal((int)ReturnLength);
                try
                {
                    if (GetLogicalProcessorInformation(Ptr, ref ReturnLength))
                    {
                        int size = Marshal.SizeOf(typeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION));
                        int len = (int)ReturnLength / size;
                        SYSTEM_LOGICAL_PROCESSOR_INFORMATION[] Buffer = new SYSTEM_LOGICAL_PROCESSOR_INFORMATION[len];
                        IntPtr Item = Ptr;
                        for (int i = 0; i < len; i++)
                        {
                            Buffer[i] = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION)Marshal.PtrToStructure(Item, typeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION));
                            Item += size;
                        }
                        return Buffer;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(Ptr);
                }
            }
            return null;
        }

        public List<int> GetPhysicalCoreIndex()
        {
            var coreRelationInfo = GetLogicalProcessorInformation()
                .Where(i => i.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                .ToList();
            var physicalCores = new List<int>();
            foreach (var info in coreRelationInfo)
            {
                var coreIndex = (int)Math.Log2((long)info.ProcessorMask & -(long)info.ProcessorMask);
                physicalCores.Add(coreIndex);
            }

            return physicalCores;
        }
    }
}