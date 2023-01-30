using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InterCoreBench.Linux;
using InterCoreBench.Windows;
using Mono.Options;

namespace InterCoreBench
{
    class Program
    {
        static int TestPeriodInMs = 5000;
        static int TestIntervalInMs = 100;
        static int TestCopyBlockSize = 256 * 1024;
        const int NoGCRegionSize = 128 * 1024 * 1024; // 128 MiB
        static bool EnableReverseBandwidthTest = false; // Maybe useful in HMP systems

        static IThreadAffinity ThreadAffinity;
        static volatile bool cancel = false;
        static int[,] latencyResultsNs;
        static ulong[,] bandwidthResultsMBpersec;

        static (ulong count, TimeSpan elapsed) DoSync(int core, ref int wait, ref int signal)
        {
            ThreadAffinity.SetAffinity(core, out var ctx);
            Thread.Yield();
            try
            {
                var sw = new Stopwatch();
                sw.Start();
                
                var sc = 0UL;
                while (!cancel)
                {
                    if (Interlocked.CompareExchange(ref wait, 0, 1) == 1)
                    {
                        signal = 1;
                        sc++;
                    }
                }

                sw.Stop();
                return (sc, sw.Elapsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return default;
            }
            finally
            {
                ThreadAffinity.ResetAffinity(ctx);
                Thread.Yield();
            }
        }

        static (ulong count, TimeSpan elapsed) DoCopy(int core, byte[] from, byte[] to, SemaphoreSlim wait, SemaphoreSlim notify, bool doCount)
        {
            ThreadAffinity.SetAffinity(core, out var ctx);
            Thread.Yield();
            try
            {
                var sw = new Stopwatch();
                sw.Start();
                
                var sc = 0UL;
                while (!cancel)
                {
                    wait.Wait();
                    Buffer.BlockCopy(from, 0, to, 0, from.Length);
                    sc++;
                    notify.Release();
                }

                sw.Stop();
                return (sc, sw.Elapsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return default;
            }
            finally
            {
                ThreadAffinity.ResetAffinity(ctx);
                Thread.Yield();
            }
        }

        static void TestSync(int c1, int c2, int p1, int p2, int testPeriodInMs, bool noOutput = false)
        {
            using (var s1 = new SemaphoreSlim(1))
            using (var s2 = new SemaphoreSlim(0))
            {
                cancel = false;
                if (!noOutput) Console.Write($"Testing latency between logical core {c1} and {c2}... ");
                int v1 = 0, v2 = 1;
                var t1 = Task.Run(() => DoSync(c1, ref v1, ref v2));
                var t2 = Task.Run(() => DoSync(c2, ref v2, ref v1));
                Thread.Sleep(testPeriodInMs);
                cancel = true;
                var (count, elapsed) = t1.GetAwaiter().GetResult();
                var (_, elapsed2) = t2.GetAwaiter().GetResult();
                if (elapsed2 < elapsed) elapsed = elapsed2;

                if (!noOutput)
                {
                    Console.WriteLine($"{elapsed.TotalMilliseconds * 1000 * 1000 / count / 2:0} ns ({count} synchronizations in {elapsed.TotalMilliseconds:0} ms)");
                    latencyResultsNs[p1, p2] = (int)(elapsed.TotalMilliseconds * 1000 * 1000 / count / 2);
                    latencyResultsNs[p2, p1] = (int)(elapsed.TotalMilliseconds * 1000 * 1000 / count / 2);
                }
            }
        }

        static unsafe void TestCopy(int c1, int c2, int p1, int p2, int testPeriodInMs, bool noOutput = false)
        {
            ThreadAffinity.SetAffinity(c1, out var ctx);
            Thread.Yield();
            using (var s1 = new SemaphoreSlim(1))
            using (var s2 = new SemaphoreSlim(0))
            {
                cancel = false;
                if (!noOutput) Console.Write($"Testing bandwidth between logical core {c1} and {c2}... ");
                byte[] c1s = new byte[TestCopyBlockSize], s = new byte[TestCopyBlockSize], c2d = new byte[TestCopyBlockSize];
                (new Random()).NextBytes(c1s);
                var t1 = Task.Run(() => DoCopy(c1, c1s, s, s1, s2, true));
                var t2 = Task.Run(() => DoCopy(c2, s, c2d, s2, s1, false));
                Thread.Sleep(testPeriodInMs);
                cancel = true;
                var (count, elapsed) = t1.GetAwaiter().GetResult();
                var (_, elapsed2) = t2.GetAwaiter().GetResult();
                if (elapsed2 < elapsed) elapsed = elapsed2;

                if (!noOutput)
                {
                    Console.WriteLine($"{(double)count * TestCopyBlockSize / 1024 / 1024 / 1024 / elapsed.TotalSeconds:0.00} GB/s ({(double)count * TestCopyBlockSize / 1024 / 1024 / 1024:0.00} GB copied in {elapsed.TotalMilliseconds:0} ms)");
                    bandwidthResultsMBpersec[p1, p2] = (ulong)(count * (ulong)TestCopyBlockSize / 1024 / 1024 / elapsed.TotalSeconds);
                    if (!EnableReverseBandwidthTest)
                    {
                        bandwidthResultsMBpersec[p2, p1] = (ulong)(count * (ulong)TestCopyBlockSize / 1024 / 1024 / elapsed.TotalSeconds);
                    }
                }
            }
            
            ThreadAffinity.ResetAffinity(ctx);
            Thread.Yield();
        }

        static void Main(string[] args)
        {
            ILogicalCoreInfo logicalCoreInfo;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                logicalCoreInfo = new WindowsLogicalCoreInfo();
                ThreadAffinity = new WindowsThreadAffinity();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                logicalCoreInfo = new LinuxLogicalCoreInfo();
                ThreadAffinity = new LinuxThreadAffinity();
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
            
            List<int> physicalCores = null;
            bool enableLatency = false;
            bool enableBandwidth = false;
            bool warmup = true;
            bool help = false;
            var optionSet = new OptionSet
            {
                { "l|test-latency", "Enable latency testing.", _ => enableLatency = true },
                { "b|test-bandwidth", "Enable bandwidth testing.", _ => enableBandwidth = true },
                { "r|reverse-copy", "Enable reverse copy testing in bandwidth tests. This may be useful in HMP systems", _ => EnableReverseBandwidthTest = true },
                { "s|block-size=", "Block size used in bandwidth testing in bytes. (Default: 256 KB)", c => TestCopyBlockSize = int.Parse(c) },
                { "c|cores=", "List of logical cores to run the program on separated by ','. Default: first logical cores in all physical cores.", c => physicalCores = c.Split(',').Select(s => int.Parse(s)).ToList() },
                { "i|interval=", "Test interval in milliseconds (Default: 100)", c => TestIntervalInMs = int.Parse(c) },
                { "d|duration=", "Test duration in milliseconds (Default: 5000)", c => TestPeriodInMs = int.Parse(c) },
                { "no-warmup", "Disable JIT warmup", _ => warmup = false },
                { "h|help", "Show this message and exit", c => help = true }
            };

            optionSet.Parse(args);

            if (help || !(enableLatency || enableBandwidth))
            {
                Console.WriteLine("InterCoreBench");
                Console.WriteLine("(c) 2022 David Huang. All Rights Reserved.");
                Console.WriteLine();
                Console.WriteLine("Usage: InterCoreBench [OPTIONS]");
                optionSet.WriteOptionDescriptions(Console.Out);
                Console.WriteLine();
                return;
            }

            if (physicalCores == null)
            {
                physicalCores = logicalCoreInfo.GetPhysicalCoreIndex();
            }

            if (physicalCores.Count < 2)
            {
                Console.WriteLine("Not enough cores to run this test");
                return;
            }

            latencyResultsNs = new int[physicalCores.Count, physicalCores.Count];
            bandwidthResultsMBpersec = new ulong[physicalCores.Count, physicalCores.Count];

            if (warmup)
            {
                Console.WriteLine("Initializing...");
                TestSync(0, 1, 0, 1, TestPeriodInMs, true);
                TestCopy(0, 1, 0, 1, TestPeriodInMs, true);
                GC.Collect(3, GCCollectionMode.Forced);
                Thread.Sleep(TestIntervalInMs);
            }


            for (var i = 0; i < physicalCores.Count - 1; i++)
            {
                for (var j = i + 1; j < physicalCores.Count; j++)
                {
                    if (enableLatency)
                    {
                        GC.TryStartNoGCRegion(NoGCRegionSize);
                        TestSync(physicalCores[i], physicalCores[j], i, j, TestPeriodInMs);
                        GC.EndNoGCRegion();
                        GC.Collect(3, GCCollectionMode.Forced);
                        Thread.Sleep(TestIntervalInMs);
                    }
                    
                    if (enableBandwidth)
                    {
                        GC.TryStartNoGCRegion(NoGCRegionSize);
                        TestCopy(physicalCores[i], physicalCores[j], i, j, TestPeriodInMs);
                        GC.EndNoGCRegion();
                        GC.Collect(3, GCCollectionMode.Forced);
                        Thread.Sleep(TestIntervalInMs);
                        
                        if (EnableReverseBandwidthTest)
                        {
                            GC.TryStartNoGCRegion(NoGCRegionSize);
                            TestCopy(physicalCores[j], physicalCores[i], j, i, TestPeriodInMs);
                            GC.EndNoGCRegion();
                            GC.Collect(3, GCCollectionMode.Forced);
                            Thread.Sleep(TestIntervalInMs);
                        }
                    }

                }
            }

            if (enableLatency)
            {
                Console.WriteLine("Latency results (ns)");
                Console.WriteLine();
                Console.WriteLine("Core ID," + string.Join(',', physicalCores));

                for (var i = 0; i < latencyResultsNs.GetLength(0); i++)
                {
                    Console.Write(physicalCores[i]);
                    for (var j = 0; j < latencyResultsNs.GetLength(1); j++)
                    {
                        if (latencyResultsNs[i, j] > 0)
                        {
                            Console.Write("," + latencyResultsNs[i, j]);
                        }
                        else
                        {
                            Console.Write(",");
                        }
                    }

                    Console.WriteLine();
                }
            }

            if (enableBandwidth)
            {
                Console.WriteLine();
                Console.WriteLine("Bandwidth results (MB/s)");
                Console.WriteLine();
                Console.WriteLine("Core ID," + string.Join(',', physicalCores));

                for (var i = 0; i < bandwidthResultsMBpersec.GetLength(0); i++)
                {
                    Console.Write(physicalCores[i]);
                    for (var j = 0; j < bandwidthResultsMBpersec.GetLength(1); j++)
                    {
                        if (bandwidthResultsMBpersec[i, j] > 0)
                        {
                            Console.Write("," + bandwidthResultsMBpersec[i, j]);
                        }
                        else
                        {
                            Console.Write(",");
                        }
                    }

                    Console.WriteLine();
                }
            }

            Console.WriteLine("Test finished, press any key to exit.");
            Console.ReadKey();
        }
    }
}
