global using static eft_dma_shared.Common.DMA.MemoryInterface;
using VmmFrost;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using eft_dma_shared.Common.Misc;
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Misc.Commercial;
using eft_dma_shared.Common.Unity.LowLevel.Hooks;

namespace eft_dma_shared.Common.DMA
{
    internal static class MemoryInterface
    {
        private static MemDMABase _memory;
        /// <summary>
        /// Limited Singleton Instance for use in this satelite assembly.
        /// </summary>
        public static MemDMABase Memory
        {
            get => _memory;
            internal set => _memory ??= value;
        }
    }
    /// <summary>
    /// DMA Memory Module.
    /// </summary>
    public abstract class MemDMABase
    {
        #region Init

        private const string _memoryMapFile = "mmap.txt";
        public const uint MAX_READ_SIZE = (uint)0x1000 * 1500;
        protected static readonly ManualResetEvent _syncProcessRunning = new(false);
        protected static readonly ManualResetEvent _syncInRaid = new(false);
        protected readonly Vmm _hVMM;
        protected uint _pid;
        protected bool _restartRadar;

        /// <summary>
        /// Current Process ID (PID).
        /// </summary>
        public uint PID => _pid;
        public ulong MonoBase { get; protected set; }
        public ulong UnityBase { get; protected set; }
        public virtual bool Starting { get; }
        public virtual bool Ready { get; }
        public virtual bool InRaid { get; }
        public virtual bool RaidHasStarted => true;

        /// <summary>
        /// Set to TRUE to restart the Radar on the next game loop cycle.
        /// </summary>
        public bool RestartRadar
        {
            set
            {
                if (InRaid)
                    _restartRadar = value;
            }
        }

        /// <summary>
        /// Vmm Handle for this DMA Connection.
        /// </summary>
        public nint VmmHandle => _hVMM;

        private MemDMABase() { }

        protected MemDMABase(FpgaAlgo fpgaAlgo, bool useMemMap)
        {
            LoneLogging.WriteLine("Initializing DMA...");
            /// Check MemProcFS Versions...
            var vmmVersion = FileVersionInfo.GetVersionInfo("vmm.dll").FileVersion;
            var lcVersion = FileVersionInfo.GetVersionInfo("leechcore.dll").FileVersion;
            string versions = $"Vmm Version: {vmmVersion}\n" +
                $"Leechcore Version: {lcVersion}";
            var initArgs = new string[] {
                "-norefresh",
                "-device",
                fpgaAlgo is FpgaAlgo.Auto ?
                    "fpga" : $"fpga://algo={(int)fpgaAlgo}",
                "-waitinitialize"};
            try
            {
                /// Begin Init...
                if (useMemMap && !File.Exists(_memoryMapFile))
                {
                    LoneLogging.WriteLine("[DMA] No MemMap, attempting to generate...");
                    _hVMM = new Vmm(initArgs);
                    var map = _hVMM.GetMemoryMap() ??
                        throw new Exception("Map_GetPhysMem FAIL");
                    var mapBytes = Encoding.ASCII.GetBytes(map);
                    if (!_hVMM.LeechCore.Command(LeechCore.LC_CMD_MEMMAP_SET, mapBytes, out _))
                        throw new Exception("LC_CMD_MEMMAP_SET FAIL");
                    File.WriteAllBytes(_memoryMapFile, mapBytes);
                }
                else
                {
                    if (useMemMap)
                    {
                        var mapArgs = new string[] { "-memmap", _memoryMapFile };
                        initArgs = initArgs.Concat(mapArgs).ToArray();
                    }
                    _hVMM = new Vmm(initArgs);
                }
                SetCustomVMMRefresh();
                MemoryInterface.Memory = this;
                LoneLogging.WriteLine("DMA Initialized!");
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine("WARNING: DMA Initialization Failed!");
                LoneLogging.WriteLine($"Reason: {ex.Message}");
                LoneLogging.WriteLine($"{versions}");

                // Log troubleshooting steps
                LoneLogging.WriteLine("===TROUBLESHOOTING===");
                LoneLogging.WriteLine("1. Reboot both your Game PC / Radar PC (This USUALLY fixes it).");
                LoneLogging.WriteLine("2. Reseat all cables/connections and make sure they are secure.");
                LoneLogging.WriteLine("3. Changed Hardware/Operating System on Game PC? Reset your DMA Config ('Options' menu in Client) and try again.");
                LoneLogging.WriteLine("4. Make sure all Setup Steps are completed (See DMA Setup Guide/FAQ for additional troubleshooting).");

                DialogResult result = MessageBox.Show(
                    "DMA Initialization Failed!\n\nCreating a dummy window for debugging?",
                    "DMA Initialization Failed!",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.Cancel || result == DialogResult.None)
                {
                    Environment.Exit(0);
                }
            }
        }

        #endregion

        #region VMM Refresh

        private readonly System.Timers.Timer _memCacheRefreshTimer = new(TimeSpan.FromMilliseconds(300));
        private readonly System.Timers.Timer _tlbRefreshTimer = new(TimeSpan.FromSeconds(2));

        /// <summary>
        /// Sets Custom VMM Refresh Timers. Be sure to FULL refresh when outside of a raid.
        /// </summary>
        private void SetCustomVMMRefresh()
        {
            _memCacheRefreshTimer.Elapsed += memCacheRefreshTimer_Elapsed;
            _tlbRefreshTimer.Elapsed += tlbRefreshTimer_Elapsed;
            _memCacheRefreshTimer.Start();
            _tlbRefreshTimer.Start();
        }

        private void memCacheRefreshTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_hVMM.SetConfig(Vmm.CONFIG_OPT_REFRESH_FREQ_MEM_PARTIAL, 1))
                LoneLogging.WriteLine("WARNING: Vmm MEM CACHE Refresh (Partial) Failed!");
        }

        private void tlbRefreshTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_hVMM == null || !_hVMM.SetConfig(Vmm.CONFIG_OPT_REFRESH_FREQ_TLB_PARTIAL, 1))
                LoneLogging.WriteLine("WARNING: Vmm TLB Refresh (Partial) Failed!");
        }

        /// <summary>
        /// Manually Force a Full Vmm Refresh.
        /// </summary>
        public void FullRefresh()
        {
            if (_hVMM == null || !_hVMM.SetConfig(Vmm.CONFIG_OPT_REFRESH_ALL, 1))
                LoneLogging.WriteLine("WARNING: Vmm FULL Refresh Failed!");
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when the game process is successfully started.
        /// Outside Subscribers should handle exceptions!
        /// </summary>
        public static event EventHandler<EventArgs> GameStarted;
        /// <summary>
        /// Raised when the game process is no longer running.
        /// Outside Subscribers should handle exceptions!
        /// </summary>
        public static event EventHandler<EventArgs> GameStopped;
        /// <summary>
        /// Raised when a raid starts.
        /// Outside Subscribers should handle exceptions!
        /// </summary>
        public static event EventHandler<EventArgs> RaidStarted;
        /// <summary>
        /// Raised when a raid ends.
        /// Outside Subscribers should handle exceptions!
        /// </summary>
        public static event EventHandler<EventArgs> RaidStopped;

        /// <summary>
        /// Raises the GameStarted Event.
        /// </summary>
        protected static void OnGameStarted()
        {
            GameStarted?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the GameStopped Event.
        /// </summary>
        protected static void OnGameStopped()
        {
            GameStopped?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the RaidStarted Event.
        /// </summary>
        protected static void OnRaidStarted()
        {
            RaidStarted?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the RaidStopped Event.
        /// </summary>
        protected static void OnRaidStopped()
        {
            RaidStopped?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Blocks indefinitely until the Game Process is Running, otherwise returns immediately.
        /// </summary>
        /// <returns>True if the Process is running, otherwise this method never returns.</returns>
        public static bool WaitForProcess() => _syncProcessRunning.WaitOne();

        /// <summary>
        /// Blocks indefinitely until In Raid/Match, otherwise returns immediately.
        /// </summary>
        /// <returns>True if In Raid/Match, otherwise this method never returns.</returns>
        public static bool WaitForRaid() => _syncInRaid.WaitOne();

        #endregion

        #region ScatterRead

        /// <summary>
        /// Performs multiple reads in one sequence, significantly faster than single reads.
        /// Designed to run without throwing unhandled exceptions, which will ensure the maximum amount of
        /// reads are completed OK even if a couple fail.
        /// </summary>
        public void ReadScatter(IScatterEntry[] entries, bool useCache = true)
        {
            if (entries.Length == 0)
                return;
            var pagesToRead = new HashSet<ulong>(entries.Length); // Will contain each unique page only once to prevent reading the same page multiple times
            foreach (var entry in entries) // First loop through all entries - GET INFO
            {
                // INTEGRITY CHECK - Make sure the read is valid and within range
                if (entry.Address == 0x0 || entry.CB == 0 || (uint)entry.CB > MAX_READ_SIZE)
                {
                    //LoneLogging.WriteLine($"[Scatter Read] Out of bounds read @ 0x{entry.Address.ToString("X")} ({entry.CB})");
                    entry.IsFailed = true;
                    continue;
                }

                // get the number of pages
                uint numPages = ADDRESS_AND_SIZE_TO_SPAN_PAGES(entry.Address, (uint)entry.CB);
                ulong basePage = PAGE_ALIGN(entry.Address);

                //loop all the pages we would need
                for (int p = 0; p < numPages; p++)
                {
                    ulong page = basePage + 0x1000 * (uint)p;
                    pagesToRead.Add(page);
                }
            }
            if (pagesToRead.Count == 0)
                return;

            uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
            using var hScatter = _hVMM.MemReadScatter2(_pid, flags, pagesToRead.ToArray());
            if (AntiPage.Initialized)
            {
                foreach (var failed in hScatter.Failed)
                    AntiPage.Register(failed, 8); // This is always one page at a time
            }

            foreach (var entry in entries) // Second loop through all entries - PARSE RESULTS
            {
                if (entry.IsFailed)
                    continue;
                entry.SetResult(hScatter);
            }
        }

        #endregion

        #region ReadMethods

        /// <summary>
        /// Prefetch pages into the cache.
        /// </summary>
        /// <param name="va"></param>
        public void ReadCache(params ulong[] va)
        {
            _hVMM.MemPrefetchPages(_pid, va);
        }

        /// <summary>
        /// Read memory into a Buffer of type <typeparamref name="T"/>
        /// </summary>
        /// <typeparam name="T">Value Type <typeparamref name="T"/></typeparam>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="buffer">Buffer to receive memory read in.</param>
        /// <param name="useCache">Use caching for this read.</param>
        public unsafe void ReadBuffer<T>(ulong addr, Span<T> buffer, bool useCache = true, bool allowPartialRead = false)
            where T : unmanaged
        {
            uint cb = (uint)(SizeChecker<T>.Size * buffer.Length);
            try
            {
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;

                if (!_hVMM.MemReadSpan(_pid, addr, buffer, out uint cbRead, flags))
                    throw new VmmException("Memory Read Failed!");

                if (cbRead == 0)
                    throw new VmmException("Memory Read Failed!");
                if (!allowPartialRead && cbRead != cb)
                    throw new VmmException("Memory Read Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, cb);
                throw;
            }
        }

        /// <summary>
        /// Read memory into a Buffer of type <typeparamref name="T"/> and ensure the read is correct.
        /// </summary>
        /// <typeparam name="T">Value Type <typeparamref name="T"/></typeparam>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="buffer1">Buffer to receive memory read in.</param>
        /// <param name="useCache">Use caching for this read.</param>
        public unsafe void ReadBufferEnsure<T>(ulong addr, Span<T> buffer1)
            where T : unmanaged
        {
            uint cb = (uint)(SizeChecker<T>.Size * buffer1.Length);
            try
            {
                var buffer2 = new T[buffer1.Length].AsSpan();
                var buffer3 = new T[buffer1.Length].AsSpan();
                uint cbRead;
                if (!_hVMM.MemReadSpan(_pid, addr, buffer3, out cbRead, Vmm.FLAG_NOCACHE))
                    throw new VmmException("Memory Read Failed!");
                if (cbRead != cb)
                    throw new VmmException("Memory Read Failed!");
                Thread.SpinWait(5);
                if (!_hVMM.MemReadSpan(_pid, addr, buffer2, out cbRead, Vmm.FLAG_NOCACHE))
                    throw new VmmException("Memory Read Failed!");
                if (cbRead != cb)
                    throw new VmmException("Memory Read Failed!");
                Thread.SpinWait(5);
                if (!_hVMM.MemReadSpan(_pid, addr, buffer1, out cbRead, Vmm.FLAG_NOCACHE))
                    throw new VmmException("Memory Read Failed!");
                if (cbRead != cb)
                    throw new VmmException("Memory Read Failed!");
                if (!buffer1.SequenceEqual(buffer2) || !buffer1.SequenceEqual(buffer3) || !buffer2.SequenceEqual(buffer3))
                {
                    throw new VmmException("Memory Read Failed!");
                }
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, cb);
                throw;
            }
        }

        /// <summary>
        /// Read a chain of pointers and get the final result.
        /// </summary>
        public ulong ReadPtrChain(ulong addr, uint[] offsets, bool useCache = true)
        {
            var pointer = addr; // push ptr to first address value
            for (var i = 0; i < offsets.Length; i++)
                pointer = ReadPtr(pointer + offsets[i], useCache);

            return pointer;
        }

        /// <summary>
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        public ulong ReadPtr(ulong addr, bool useCache = true)
        {
            var pointer = ReadValue<ulong>(addr, useCache);
            pointer.ThrowIfInvalidVirtualAddress();
            return pointer;
        }

        /// <summary>
        /// Read value type/struct from specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public unsafe T ReadValue<T>(ulong addr, bool useCache = true)
            where T : unmanaged, allows ref struct
        {
            try
            {
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                return _hVMM.MemReadAs<T>(_pid, addr, flags);
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)sizeof(T));
                throw;
            }
        }

        /// <summary>
        /// Read byref value type/struct from specified address.
        /// Result returned byref.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public unsafe void ReadValue<T>(ulong addr, out T result, bool useCache = true)
            where T : unmanaged, allows ref struct
        {
            try
            {
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                _hVMM.MemReadAs<T>(_pid, addr, out result, flags);
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)sizeof(T));
                throw;
            }
        }

        /// <summary>
        /// Read value type/struct from specified address multiple times to ensure the read is correct.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public unsafe T ReadValueEnsure<T>(ulong addr)
            where T : unmanaged, allows ref struct
        {
            int cb = sizeof(T);
            try
            {
                T r1 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                Thread.SpinWait(5);
                T r2 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                Thread.SpinWait(5);
                T r3 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                var b1 = new ReadOnlySpan<byte>(&r1, cb);
                var b2 = new ReadOnlySpan<byte>(&r2, cb);
                var b3 = new ReadOnlySpan<byte>(&r3, cb);
                if (!b1.SequenceEqual(b2) || !b1.SequenceEqual(b3) || !b2.SequenceEqual(b3))
                {
                    throw new VmmException("Memory Read Failed!");
                }
                return r1;
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)cb);
                throw;
            }
        }

        /// <summary>
        /// Read byref value type/struct from specified address multiple times to ensure the read is correct.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public unsafe void ReadValueEnsure<T>(ulong addr, out T result)
            where T : unmanaged, allows ref struct
        {
            int cb = sizeof(T);
            try
            {
                T r1 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                Thread.SpinWait(5);
                T r2 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                Thread.SpinWait(5);
                T r3 = _hVMM.MemReadAs<T>(_pid, addr, Vmm.FLAG_NOCACHE);
                var b1 = new ReadOnlySpan<byte>(&r1, cb);
                var b2 = new ReadOnlySpan<byte>(&r2, cb);
                var b3 = new ReadOnlySpan<byte>(&r3, cb);
                if (!b1.SequenceEqual(b2) || !b1.SequenceEqual(b3) || !b2.SequenceEqual(b3))
                {
                    throw new VmmException("Memory Read Failed!");
                }
                result = r1;
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)cb);
                throw;
            }
        }

        /// <summary>
        /// Read null terminated string (utf-8/default).
        /// </summary>
        /// <param name="length">Number of bytes to read.</param>
        /// <exception cref="Exception"></exception>
        public string ReadString(ulong addr, int length, bool useCache = true) // read n bytes (string)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, (int)0x1000, nameof(length));
            Span<byte> buffer = stackalloc byte[length];
            buffer.Clear();
            ReadBuffer(addr, buffer, useCache, true);
            var nullIndex = buffer.IndexOf((byte)0);
            return nullIndex >= 0
                ? Encoding.UTF8.GetString(buffer.Slice(0, nullIndex))
                : Encoding.UTF8.GetString(buffer);
        }

        /// <summary>
        /// Read UnityEngineString structure
        /// </summary>
        public string ReadUnityString(ulong addr, int length = 64, bool useCache = true)
        {
            if (length % 2 != 0)
                length++;
            length *= 2; // Unicode 2 bytes per char
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, (int)0x1000, nameof(length));
            Span<byte> buffer = stackalloc byte[length];
            buffer.Clear();
            ReadBuffer(addr + 0x14, buffer, useCache, true);
            var nullIndex = buffer.FindUtf16NullTerminatorIndex();
            return nullIndex >= 0
                ? Encoding.Unicode.GetString(buffer.Slice(0, nullIndex))
                : Encoding.Unicode.GetString(buffer);
        }

        #endregion

        #region WriteMethods

        /// <summary>
        /// Write value type/struct to specified address, and ensure it is written.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public unsafe void WriteValueEnsure<T>(ulong addr, T value)
            where T : unmanaged, allows ref struct
        {
            int cb = sizeof(T);
            try
            {
                var b1 = new ReadOnlySpan<byte>(&value, cb);
                const int retryCount = 3;
                for (int i = 0; i < retryCount; i++)
                {
                    try
                    {
                        WriteValue(addr, value);
                        Thread.SpinWait(5);
                        T temp = ReadValue<T>(addr, false);
                        var b2 = new ReadOnlySpan<byte>(&temp, cb);
                        if (b1.SequenceEqual(b2))
                        {
                            return; // SUCCESS
                        }
                    }
                    catch { }
                }
                throw new VmmException("Memory Write Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)cb);
                throw;
            }
        }

        /// <summary>
        /// Write byref value type/struct to specified address, and ensure it is written.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public unsafe void WriteValueEnsure<T>(ulong addr, ref T value)
            where T : unmanaged, allows ref struct
        {
            int cb = sizeof(T);
            try
            {
                fixed (void* pb = &value)
                {
                    var b1 = new ReadOnlySpan<byte>(pb, cb);
                    const int retryCount = 3;
                    for (int i = 0; i < retryCount; i++)
                    {
                        try
                        {
                            WriteValue(addr, ref value);
                            Thread.SpinWait(5);
                            T temp = ReadValue<T>(addr, false);
                            var b2 = new ReadOnlySpan<byte>(&temp, cb);
                            if (b1.SequenceEqual(b2))
                            {
                                return; // SUCCESS
                            }
                        }
                        catch { }
                    }
                    throw new VmmException("Memory Write Failed!");
                }
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)cb);
                throw;
            }
        }

        /// <summary>
        /// Write value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public unsafe void WriteValue<T>(ulong addr, T value)
            where T : unmanaged, allows ref struct
        {
            if (!SharedProgram.Config?.MemWritesEnabled ?? false)
                throw new Exception("Memory Writing is Disabled!");
            try
            {
                if (!_hVMM.MemWriteAs(_pid, addr, value))
                    throw new VmmException("Memory Write Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)sizeof(T));
                throw;
            }
        }

        /// <summary>
        /// Write byref value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public unsafe void WriteValue<T>(ulong addr, ref T value)
            where T : unmanaged, allows ref struct
        {
            if (!SharedProgram.Config?.MemWritesEnabled ?? false)
                throw new Exception("Memory Writing is Disabled!");
            try
            {
                if (!_hVMM.MemWriteAs(_pid, addr, ref value))
                    throw new VmmException("Memory Write Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)sizeof(T));
                throw;
            }
        }

        /// <summary>
        /// Write byte array buffer to Memory Address.
        /// </summary>
        /// <param name="addr">Address to write to.</param>
        /// <param name="buffer">Buffer to write.</param>
        public unsafe void WriteBuffer<T>(ulong addr, Span<T> buffer)
            where T : unmanaged
        {
            if (!SharedProgram.Config?.MemWritesEnabled ?? false)
                throw new Exception("Memory Writing is Disabled!");
            try
            {
                if (!_hVMM.MemWriteSpan(_pid, addr, buffer))
                    throw new VmmException("Memory Write Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)sizeof(T));
                throw;
            }
        }

        /// <summary>
        /// Write a buffer to the specified address and validate the right bytes were written.
        /// </summary>
        /// <param name="addr">Address to write to.</param>
        /// <param name="buffer">Buffer to write.</param>
        public void WriteBufferEnsure<T>(ulong addr, Span<T> buffer)
            where T : unmanaged
        {
            int cb = SizeChecker<T>.Size * buffer.Length;
            try
            {
                Span<byte> temp = cb > 0x1000 ? new byte[cb] : stackalloc byte[cb];
                ReadOnlySpan<byte> b1 = MemoryMarshal.Cast<T, byte>(buffer);
                const int retryCount = 3;
                for (int i = 0; i < retryCount; i++)
                {
                    try
                    {
                        WriteBuffer(addr, buffer);
                        Thread.SpinWait(5);
                        temp.Clear();
                        ReadBuffer(addr, temp, false, false);
                        if (temp.SequenceEqual(b1))
                        {
                            return; // SUCCESS
                        }
                    }
                    catch { }
                }
                throw new VmmException("Memory Write Failed!");
            }
            catch (VmmException)
            {
                if (AntiPage.Initialized)
                    AntiPage.Register(addr, (uint)cb);
                throw;
            }
        }

        #endregion

        #region Misc

        /// <summary>
        /// Get an Export from this process.
        /// </summary>
        /// <param name="module"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public ulong GetExport(string module, string name)
        {
            var export = _hVMM.ProcessGetProcAddress(_pid, module, name);
            export.ThrowIfInvalidVirtualAddress();
            return export;
        }

        /// <summary>
        /// Close the FPGA Connection.
        /// </summary>
        public void CloseFPGA() => _hVMM?.Close();

        /// <summary>
        /// Get a Vmm Scatter Handle.
        /// </summary>
        /// <param name="flags"></param>
        /// <param name="pid"></param>
        /// <returns></returns>
        public VmmScatter GetScatter(uint flags, out uint pid)
        {
            pid = _pid;
            var handle = _hVMM.Scatter_Initialize(pid, flags);
            ArgumentNullException.ThrowIfNull(handle, nameof(handle));
            return handle;
        }

        #endregion

        #region Memory Macros

        /// <summary>
        /// The PAGE_ALIGN macro takes a virtual address and returns a page-aligned
        /// virtual address for that page.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PAGE_ALIGN(ulong va) => va & ~(0x1000ul - 1);

        /// <summary>
        /// The ADDRESS_AND_SIZE_TO_SPAN_PAGES macro takes a virtual address and size and returns the number of pages spanned by
        /// the size.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ADDRESS_AND_SIZE_TO_SPAN_PAGES(ulong va, uint size) =>
            (uint)(BYTE_OFFSET(va) + size + (0x1000ul - 1) >> (int)12);

        /// <summary>
        /// The BYTE_OFFSET macro takes a virtual address and returns the byte offset
        /// of that address within the page.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint BYTE_OFFSET(ulong va) => (uint)(va & 0x1000ul - 1);

        /// <summary>
        /// Returns a length aligned to 8 bytes.
        /// Always rounds up.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint AlignLength(uint length) => (length + 7) & ~7u;

        /// <summary>
        /// Returns an address aligned to 8 bytes.
        /// Always the next aligned address.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignAddress(ulong address) => (address + 7) & ~7ul;

        #endregion

        #region NativeHook Interop
        /// <summary>
        /// Get the Code Cave Address for NativeHook.
        /// </summary>
        /// <exception cref="Exception"></exception>
        public virtual ulong GetCodeCave() => throw new NotImplementedException();
        #endregion
    }
}