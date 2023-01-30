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
        const int NoGCRegionSize = 128 * 1024 * 1024; // 128 MiB

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

        static void TestSync(int c1, int c2, int p1, int p2, int testPeriodInMs, int testIterations, bool noOutput = false)
        {
            if (!noOutput) Console.Write($"Testing latency between logical core {c1} and {c2}... ");
            var results = new List<(ulong Count, TimeSpan Elapsed)>();
            for (var i = 0; i < testIterations; i++)
            {
                cancel = false;
                int v1 = 0, v2 = 1;
                var t1 = Task.Run(() => DoSync(c1, ref v1, ref v2));
                var t2 = Task.Run(() => DoSync(c2, ref v2, ref v1));
                Thread.Sleep(testPeriodInMs);
                cancel = true;
                var (c, e) = t1.GetAwaiter().GetResult();
                var (_, e2) = t2.GetAwaiter().GetResult();
                if (e2 < e) e = e2;
                results.Add((c, e));
            }

            var (count, elapsed) = results.MaxBy(s => (double)s.Count / s.Elapsed.TotalMilliseconds);
            if (!noOutput)
            {
                Console.WriteLine($"{elapsed.TotalMilliseconds * 1000 * 1000 / count / 2:0} ns ({count} synchronizations in {elapsed.TotalMilliseconds:0} ms)");
                latencyResultsNs[p1, p2] = (int)(elapsed.TotalMilliseconds * 1000 * 1000 / count / 2);
                latencyResultsNs[p2, p1] = (int)(elapsed.TotalMilliseconds * 1000 * 1000 / count / 2);
            }
        }

        static unsafe void TestCopy(int c1, int c2, int p1, int p2, int testCopyBlockSize, int testPeriodInMs, int testIterations, bool enableReverseBandwidthTest, bool noOutput = false)
        {
            if (!noOutput) Console.Write($"Testing bandwidth between logical core {c1} and {c2}... ");
            ThreadAffinity.SetAffinity(c1, out var ctx);
            Thread.Yield();
            var results = new List<(ulong Count, TimeSpan Elapsed)>();
            for (var i = 0; i < testIterations; i++)
            {
                using (var s1 = new SemaphoreSlim(1))
                using (var s2 = new SemaphoreSlim(0))
                {
                    cancel = false;
                    byte[] c1s = new byte[testCopyBlockSize], s = new byte[testCopyBlockSize], c2d = new byte[testCopyBlockSize];
                    (new Random()).NextBytes(c1s);
                    var t1 = Task.Run(() => DoCopy(c1, c1s, s, s1, s2, true));
                    var t2 = Task.Run(() => DoCopy(c2, s, c2d, s2, s1, false));
                    Thread.Sleep(testPeriodInMs);
                    cancel = true;
                    var (c, e) = t1.GetAwaiter().GetResult();
                    var (_, e2) = t2.GetAwaiter().GetResult();
                    if (e2 < e) e = e2;
                    results.Add((c, e));
                }
            }
            
            ThreadAffinity.ResetAffinity(ctx);
            Thread.Yield();
            if (!noOutput)
            {
                var (count, elapsed) = results.MaxBy(s => (double)s.Count / s.Elapsed.TotalMilliseconds);
                Console.WriteLine($"{(double)count * testCopyBlockSize / 1024 / 1024 / 1024 / elapsed.TotalSeconds:0.00} GB/s ({(double)count * testCopyBlockSize / 1024 / 1024 / 1024:0.00} GB copied in {elapsed.TotalMilliseconds:0} ms)");
                bandwidthResultsMBpersec[p1, p2] = (ulong)(count * (ulong)testCopyBlockSize / 1024 / 1024 / elapsed.TotalSeconds);
                if (!enableReverseBandwidthTest)
                {
                    bandwidthResultsMBpersec[p2, p1] = (ulong)(count * (ulong)testCopyBlockSize / 1024 / 1024 / elapsed.TotalSeconds);
                }
            }
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
            var testIterations = 5;
            int testPeriodInMs = 1000;
            int testIntervalInMs = 100;
            int testCopyBlockSize = 256 * 1024;
            bool enableReverseBandwidthTest = false;
            var optionSet = new OptionSet
            {
                { "l|test-latency", "Enable latency testing.", _ => enableLatency = true },
                { "b|test-bandwidth", "Enable bandwidth testing.", _ => enableBandwidth = true },
                { "r|reverse-copy", "Enable reverse copy testing in bandwidth tests. This may be useful in HMP systems.", _ => enableReverseBandwidthTest = true },
                { "s|block-size=", "Block size used in bandwidth testing in bytes. (Default: 256 KB)", c => testCopyBlockSize = int.Parse(c) },
                { "c|cores=", "List of logical cores to run the program on separated by ','. Default: first logical core of every physical core.", c => physicalCores = c.Split(',').Select(s => int.Parse(s)).ToList() },
                { "i|interval=", "Test interval in milliseconds. (Default: 100)", c => testIntervalInMs = int.Parse(c) },
                { "d|duration=", "Test duration in milliseconds. (Default: 1000)", c => testPeriodInMs = int.Parse(c) },
                { "t|iterations=", "Test iterations to take the best result from. (Default: 5)", c => testIterations = int.Parse(c) },
                { "no-warmup", "Disable JIT warm up.", _ => warmup = false },
                { "h|help", "Show this message and exit.", c => help = true }
            };

            optionSet.Parse(args);

            if (help || !(enableLatency || enableBandwidth))
            {
                Console.WriteLine("InterCoreBench");
                Console.WriteLine("(c) 2023 David Huang. All Rights Reserved.");
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
                TestSync(0, 1, 0, 1, testPeriodInMs, 1, true);
                TestCopy(0, 1, 0, 1, testCopyBlockSize, testPeriodInMs, 1, false, true);
                GC.Collect(3, GCCollectionMode.Forced);
                Thread.Sleep(testIntervalInMs);
            }


            for (var i = 0; i < physicalCores.Count - 1; i++)
            {
                for (var j = i + 1; j < physicalCores.Count; j++)
                {
                    if (enableLatency)
                    {
                        GC.TryStartNoGCRegion(NoGCRegionSize);
                        TestSync(physicalCores[i], physicalCores[j], i, j, testPeriodInMs, testIterations);
                        GC.EndNoGCRegion();
                        GC.Collect(3, GCCollectionMode.Forced);
                        Thread.Sleep(testIntervalInMs);
                    }
                    
                    if (enableBandwidth)
                    {
                        GC.TryStartNoGCRegion(NoGCRegionSize);
                        TestCopy(physicalCores[i], physicalCores[j], i, j, testCopyBlockSize, testPeriodInMs, testIterations, enableReverseBandwidthTest);
                        GC.EndNoGCRegion();
                        GC.Collect(3, GCCollectionMode.Forced);
                        Thread.Sleep(testIntervalInMs);
                        
                        if (enableReverseBandwidthTest)
                        {
                            GC.TryStartNoGCRegion(NoGCRegionSize);
                            TestCopy(physicalCores[j], physicalCores[i], j, i, testCopyBlockSize, testPeriodInMs, testIterations, enableReverseBandwidthTest);
                            GC.EndNoGCRegion();
                            GC.Collect(3, GCCollectionMode.Forced);
                            Thread.Sleep(testIntervalInMs);
                        }
                    }

                }
            }

            if (enableLatency)
            {
                Console.WriteLine();
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
