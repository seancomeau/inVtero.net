﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using inVtero.net.Support;

namespace inVtero.net
{
    /// <summary>
    /// Scanner is the initial entrypoint into inVtero, the most basic and primary functonality
    /// 
    /// </summary>
    public class Scanner
    {
        // for diagnostic printf's
        const int MAX_LINE_WIDTH = 120;

        // using bag since it has the same collection interface as List
        public ConcurrentDictionary<long, DetectedProc> DetectedProcesses;
        //public ParallelQuery<KeyValuePair<long, DetectedProc>> VMCSScanSet;
        public DetectedProc[] VMCSScanSet;

        #region class instance variables
        public string Filename;
        public long FileSize;
        public List<VMCS> HVLayer;
        public bool DumpVMCSPage;

        PTType HostOS;
        List<MemoryRun> Gaps;

        List<Func<long, bool>> CheckMethods;
        PTType scanMode;
        public PTType ScanMode
        {
            get { return scanMode; }
            set
            {
                scanMode = value;

                CheckMethods.Clear();

                if ((value & PTType.GENERIC) == PTType.GENERIC)
                    CheckMethods.Add(Generic);

                if ((value & PTType.Windows) == PTType.Windows)
                    CheckMethods.Add(Windows);

                if ((value & PTType.HyperV) == PTType.HyperV)
                    CheckMethods.Add(HV);
        
                if ((value & PTType.FreeBSD) == PTType.FreeBSD)
                    CheckMethods.Add(FreeBSD);

                if ((value & PTType.OpenBSD) == PTType.OpenBSD)
                    CheckMethods.Add(OpenBSD);

                if ((value & PTType.NetBSD) == PTType.NetBSD)
                    CheckMethods.Add(NetBSD);

                if ((value & PTType.VMCS) == PTType.VMCS)
                    CheckMethods.Add(VMCS);
            }
        }

        #endregion

        public Scanner(string InputFile)
        {
            DetectedProcesses = new ConcurrentDictionary<long, DetectedProc>();
            HVLayer = new List<VMCS>();
            Filename = InputFile;
            FileSize = 0;
            Gaps = new List<MemoryRun>();
            CheckMethods = new List<Func<long, bool>>();
        }

        /// <summary>
        /// The VMCS scan is based on the LINK pointer, abort code and CR3 register
        /// We  later isolate the EPTP based on constraints for that pointer
        /// </summary>
        /// <param name="offset"></param>
        /// <returns>true if the page being scanned is a candidate</returns>
        public bool VMCS(long xoffset)
        {
            var RevID = (REVISION_ID)(block[0] & 0xffffffff);
            var Acode = (VMCS_ABORT)((block[0] >> 32) & 0x7fffffff);

            var KnownAbortCode = false;
            var KnownRevision = false;
            var Candidate = false;
            var LinkCount = 0;
            var Neg1 = -1;

            if(VMCSScanSet == null)
                throw new NullReferenceException("Entered VMCS callback w/o having found any VMCS, this is a second pass Func") ;

            // this might be a bit micro-opt-pointless ;)
            //Parallel.Invoke(() =>
            //{
            KnownRevision = typeof(REVISION_ID).GetEnumValues().Cast<REVISION_ID>().Any(x => x == RevID);
            //}, () =>
            //{
            KnownAbortCode = typeof(VMCS_ABORT).GetEnumValues().Cast<VMCS_ABORT>().Any(x => x == Acode);
            //});
            // Find a 64bit value for link ptr
            for (int l = 0; l < block.Length; l++)
            {
                if (block[l] == Neg1)
                    LinkCount++;

                // too many
                if (LinkCount > 32)
                    return false;
            }
            // We expect to have 1 Link pointer at least
            if (LinkCount == 0 || !KnownAbortCode)
                return false;

            // curr width of line to screen
            Candidate = false;
            Parallel.For(0, VMCSScanSet.Length, (v) =>
            {
                var vmcs_entry = VMCSScanSet[v];

                for (int check = 1; check < block.Length; check++)
                {
                    if (block[check] == vmcs_entry.CR3Value && Candidate == false)
                    {
                        var OutputList = new List<long>();
                        StringBuilder sb = null, sbRED = null;
                        byte[] shorted = null;
                        var curr_width = 0;

                        if (Vtero.VerboseOutput)
                        {
                            sb = new StringBuilder();
                            // reverse endianess for easy reading in hex dumps/editors
                            shorted = BitConverter.GetBytes(block[check]);
                            Array.Reverse(shorted, 0, 8);
                            var Converted = BitConverter.ToUInt64(shorted, 0);

                            sbRED = new StringBuilder();
                            sbRED.Append($"Hypervisor: VMCS revision field: {RevID} [{((uint)RevID):X8}] abort indicator: {Acode} [{((int)Acode):X8}]{Environment.NewLine}");
                            sbRED.Append($"Hypervisor: {vmcs_entry.PageTableType} CR3 found [{vmcs_entry.CR3Value:X16})] byte-swapped: [{Converted:X16}] @ PAGE/File Offset = [{xoffset:X16}]");
                        }

                        for (int i = 0; i < block.Length; i++)
                        {
                            var eptp = new EPTP(block[i]);

                            // any good minimum size? 64kb?
                            if (block[i] > 0
                            && block[i] < FileSize
                            && eptp.IsFullyValidated()
                            && !OutputList.Contains(block[i]))
                            {
                                Candidate = true;
                                OutputList.Add(block[i]);

                                if (Vtero.VerboseOutput)
                                {
                                    var linefrag = $"[{i}][{block[i]:X16}] ";

                                    if (curr_width + linefrag.Length > MAX_LINE_WIDTH)
                                    {
                                        sb.Append(Environment.NewLine);
                                        curr_width = 0;
                                    }
                                    sb.Append(linefrag);
                                    curr_width += linefrag.Length;
                                }

                            }
                        }
                        if (Candidate && Vtero.VerboseOutput)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(sbRED.ToString());
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine(sb.ToString());
                        }

                        VMCS vmcsFound = null;
                        // most VMWare I've scanned comes are using this layout
                        if (RevID == REVISION_ID.VMWARE_NESTED && OutputList.Contains(block[14]))
                            vmcsFound = new VMCS { dp = vmcs_entry, EPTP = block[14], gCR3 = vmcs_entry.CR3Value };
                        else if (OutputList.Count() == 1)
                            vmcsFound = new net.VMCS { dp = vmcs_entry, EPTP = OutputList[0], gCR3 = vmcs_entry.CR3Value };

                        if (vmcsFound != null)
                            HVLayer.Add(vmcsFound);
                    }
                }
            });
            return Candidate;
        }

        /// <summary>
        /// TODO: NetBSD needs some analysis
        /// Will add more later, this check is a bit noisy, consider it alpha
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool NetBSD(long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[255] & 0xFFFFFFFFF000);
            var diff = offset - shifted;


            if (((block[511] & 0xf3) == 0x63) && ((0x63 == (block[320] & 0xf3)) || (0x63 == (block[256] & 0xf3))))
            {
                if (((block[255] & 0xf3) == 0x63) && (0 == (block[255] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.NetBSD };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[p] != 0)
                                dp.TopPageTablePage.Add(p, block[p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if(Vtero.VerboseOutput)
                            Console.WriteLine(dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /*   OpenBSD /src/sys/arch/amd64/include/pmap.h
            #define L4_SLOT_PTE		255
            #define L4_SLOT_KERN		256
            #define L4_SLOT_KERNBASE	511
            #define L4_SLOT_DIRECT		510
        */
        /// <summary>
        /// Slighyly better check then NetBSD so I guess consider it beta!
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool OpenBSD(long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[255] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            if (((block[510] & 0xf3) == 0x63) && (0x63 == (block[256] & 0xf3)) && (0x63 == (block[254] & 0xf3)))
            {
                if (((block[255] & 0xf3) == 0x63) && (0 == (block[255] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.OpenBSD };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[p] != 0)
                                dp.TopPageTablePage.Add(p, block[p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            Console.WriteLine(dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /// <summary>
        /// The FreeBSD check for process detection is good
        /// Consider it release quality ;) 
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool FreeBSD(long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[0x100] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            if (((block[0] & 0xff) == 0x67) && (0x67 == (block[0xff] & 0xff)))
            {
                if (((block[0x100] & 0xff) == 0x63) && (0 == (block[0x100] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.FreeBSD };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[p] != 0)
                                dp.TopPageTablePage.Add(p, block[p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            Console.WriteLine(dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }
        /// <summary>
        /// Naturally the Generic checker is fairly chatty but at least you can use it
        /// to find unknowns, we could use some more tuneables here to help select the
        /// best match, I currently use the value with the lowest diff, which can be correct
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool Generic(long offset)
        {
            var Candidate = false;
            //var offset = CurrWindowBase + CurrMapBase;
            long bestShift = long.MaxValue, bestDiff = long.MaxValue;
            var bestOffset = long.MaxValue;
            var i = 0x1ff;

            do
            {
                if (((block[0] & 0xff) == 0x63) && block[0x1ff] == 0)
                {
                    if (((block[i] & 0xff) == 0x63 || (block[i] & 0xff) == 0x67))
                    {
                        // we disqualify entries that have these bits configured
                        // 111 1111 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                        // 
                        if ((block[i] & 0x7FFF000000000480) == 0)
                        {
                            var shifted = (block[i] & 0xFFFFFFFFF000);

                            if (shifted != 0 && shifted < FileSize)
                            {
                                var diff = offset - shifted;

                                if (diff < bestDiff)
                                {
                                    bestShift = shifted;
                                    bestDiff = diff;
                                    bestOffset = offset;
                                }
                                if (!DetectedProcesses.ContainsKey(offset))
                                {
                                    var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.GENERIC };
                                    for (int p = 0; p < 0x200; p++)
                                    {
                                        if (block[p] != 0)
                                            dp.TopPageTablePage.Add(p, block[p]);
                                    }

                                    DetectedProcesses.TryAdd(offset, dp);
                                    if (Vtero.VerboseOutput)
                                        Console.WriteLine(dp.ToString());
                                    Candidate = true;
                                }
                            }
                        }
                    }
                }
                i--;
                // maybe some kernels keep more than 1/2 system memory 
                // wouldn't that be a bit greedy though!?
            } while (i > 0xFF); 
            return Candidate;
        }

        /// <summary>
        /// In some deployments Hyper-V was found to use a configuration as such
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool HV(long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[0x1fe] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            // detect mode 2, 2 seems good for most purposes and is more portable
            // maybe 0x3 is sufficient
            if (shifted != 0 && ((block[0] & 0xfff) == 0x063) && ((block[0x1fe] & 0xff) == 0x63 || (block[0x1fe] & 0xff) == 0x67) && block[0x1ff] == 0)
            {
                // we disqualify entries that have these bits configured
                // 111 1111 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                // 
                if (((ulong) block[0x1fe] & 0xFFFF000000000480) == 0)
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.HyperV };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[p] != 0)
                                dp.TopPageTablePage.Add(p, block[p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            Console.WriteLine(dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /// <summary>
        /// This is the same check as the earlier process detection code from CSW and DefCon
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool Windows(long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[0x1ed] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            // detect mode 2, 2 seems good for most purposes and is more portable
            // maybe 0x3 is sufficient
            if (((block[0] & 0xfdf) == 0x847) && ((block[0x1ed] & 0xff) == 0x63 || (block[0x1ed] & 0xff) == 0x67))
            {
                // we disqualify entries that have these bits configured
                //111 1111 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                if ((block[0x1ed] & 0x7FFF000000000480) == 0)
                {
#if MODE_1
                    if (!SetDiff)
                    {
                        FirstDiff = diff;
                        SetDiff = true;
                    }
#endif
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.Windows };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[p] != 0)
                                dp.TopPageTablePage.Add(p, block[p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            Console.WriteLine(dp.ToString());
                        Candidate = true;
                    }
                }
            }
            // mode 1 is implmented to hit on very few supported bits
            // developing a version close to this that will work for linux
            #region MODE 1 IS PRETTY LOOSE
#if MODE_1
            else
                /// detect MODE 1, we can probably get away with even just testing & 1, the valid bit
                //if (((block[0] & 3) == 3) && (block[0x1ed] & 3) == 3)		
                if ((block[0] & 1) == 1 && (block[0xf68 / 8] & 1) == 1)
            {
                // a posssible kernel first PFN? should look somewhat valid... 
                if (!SetDiff)
                {
                    // I guess we could be attacked here too, the system kernel could be modified/hooked/bootkit enough 
                    // we'll see if we need to analyze this in the long run
                    // the idea of mode 1 is a very low bit-scan, but we also do not want to mess up FirstDiff
                    // these root entries are valid for all win64's for PTE/hyper/session space etc.
                    if ((block[0xf78 / 8] & 1) == 1 && (block[0xf80 / 8] & 1) == 1 && (block[0xff8 / 8] & 1) == 1 && (block[0xff0 / 8] == 0))
                    {
                        // W/O this we may see some false positives 
                        // however can remove if you feel aggressive
                        if (diff < FileSize && (offset > shifted ? (diff + shifted == offset) : (diff + offset == shifted)))
                        {
                            FirstDiff = diff;
                            SetDiff = true;
                        }
                    }
                }

                if (SetDiff &&
                    !(FirstDiff != diff) &&
                     (shifted < (FileSize + diff)
                     //|| shifted != 0
                     ))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 1, PageTableType = PTType.Windows };

                        DetectedProcesses.TryAdd(offset, dp);
                        Console.WriteLine(dp);

                        Candidate = true;
                    }
                }
            }
#endif
            #endregion
            return Candidate;
        }

        // scanner related
        //static long offset;
        static long CurrMapBase;
        static long CurrWindowBase;
        static long mapSize = (64 * 1024 * 1024);
        static long[] block = new long[512];
        static long[][] buffers = { new long[512], new long[512] };
        static int filled = 0;

        /// <summary>
        /// A simple memory mapped scan over the input provided in the constructor
        /// </summary>
        /// <param name="Checkers">a List of Func which return bool if the current page is a candidate</param>
        /// <param name="ExitAfter">Optionally stop checking or exit early after this many candidates.  0 does not exit early.</param>
        /// <returns></returns>
        public int Analyze(int ExitAfter = 0)
        {
            var rv = 0x0;

            CurrWindowBase = 0;
            mapSize = (64 * 1024 * 1024);

            if (File.Exists(Filename))
            {
                using (var fs = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var mapName = Path.GetFileNameWithoutExtension(Filename) + DateTime.Now.ToBinary().ToString("X16");
                    using (var mmap =
                        MemoryMappedFile.CreateFromFile(fs,
                        mapName,
                        0,
                        MemoryMappedFileAccess.Read,
                        null,
                        HandleInheritability.Inheritable,
                        false))
                    {
                        var fi = new FileInfo(Filename);
                        FileSize = fi.Length;

                        while (CurrWindowBase < FileSize)
                        {
                            using (var reader = mmap.CreateViewAccessor(CurrWindowBase, mapSize, MemoryMappedFileAccess.Read))
                            {
                                CurrMapBase = 0;
                                reader.ReadArray(CurrMapBase, buffers[filled], 0, 512);

                                while (CurrMapBase < mapSize)
                                {
                                    var offset = CurrWindowBase + CurrMapBase;

                                    // next page, may be faster with larger chunks but it's simple to view 1 page at a time
                                    CurrMapBase += 4096;

                                    block = buffers[filled];
                                    filled ^= 1;

#pragma warning disable HeapAnalyzerImplicitParamsRule // Array allocation for params parameter
                                    Parallel.Invoke(() =>
                                    Parallel.ForEach(
                                        CheckMethods,
                                            () => offset,
                                            (check, parall, init_offset) =>
                                            {
                                                if (check(init_offset))
                                                    return 1;
                                                return 0;
                                            },
                                            (candidate) => Interlocked.Add(ref rv, (int) candidate)),
                                        () => {
                                            if (CurrMapBase < mapSize)
                                                UnsafeHelp.ReadBytes(reader, CurrMapBase, ref buffers[filled]);
                                        }
                                    );
                                    if (ExitAfter > 0 && rv == ExitAfter)
                                        return rv;

                                    var progress = Convert.ToInt32((Convert.ToDouble(CurrWindowBase) / Convert.ToDouble(FileSize) * 100.0) + 0.5);
                                    if (progress != ProgressBarz.Progress)
                                        ProgressBarz.RenderConsoleProgress(progress);
                                }
                            } // close current window

                            CurrWindowBase += CurrMapBase;

                            if (CurrWindowBase + mapSize > FileSize)
                                mapSize = FileSize - CurrWindowBase;
                        }
                    }
                } // close map
            } // close stream
            return rv;
        }
       
    }
}