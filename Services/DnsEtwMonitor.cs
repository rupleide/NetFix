using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace NetFix.Services;

public sealed class DnsEtwMonitor : IDisposable
{
    private static readonly Guid DnsClientProviderGuid = new("1C950233-BE20-4979-8AEE-7DC084E6E83A");
    private const string SessionName = "NetFixDnsEtwSession";

    private ulong _sessionHandle;
    private ulong _traceHandle = ulong.MaxValue;
    private Thread? _traceThread;
    private bool _isDisposed;
    private bool _isRunning;
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<(int Pid, string Ip), string> _pidIpToDomain = new();
    private readonly ConcurrentDictionary<string, string> _ipToDomain = new();
    private readonly ConcurrentDictionary<int, ConcurrentBag<string>> _pidRecentQueries = new();

    private EventRecordCallback? _eventRecordCallback;

    public bool IsRunning => _isRunning;

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning || _isDisposed) return;

            try
            {
                StopExistingSession(SessionName);

                int propSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + (SessionName.Length + 1) * 2 + 512;
                IntPtr propBuffer = Marshal.AllocHGlobal(propSize);
                try
                {
                    for (int i = 0; i < propSize; i++)
                    {
                        Marshal.WriteByte(propBuffer, i, 0);
                    }

                    var prop = new EVENT_TRACE_PROPERTIES
                    {
                        Wnode = new WNODE_HEADER
                        {
                            BufferSize = (uint)propSize,
                            Flags = 0x00020000,
                            ClientContext = 1
                        },
                        LogFileMode = 0x00000100,
                        LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
                        LogFileNameOffset = 0
                    };

                    Marshal.StructureToPtr(prop, propBuffer, false);

                    uint status = StartTraceW(out _sessionHandle, SessionName, propBuffer);
                    if (status != 0 && status != 183)
                    {
                        return;
                    }

                    var enableParams = new ENABLE_TRACE_PARAMETERS
                    {
                        Version = 1,
                        EnableProperty = 0
                    };

                    var providerGuid = DnsClientProviderGuid;
                    EnableTraceEx2(_sessionHandle, ref providerGuid, 1, 5, 0, 0, 0, ref enableParams);

                    _eventRecordCallback = ProcessEventRecord;

                    _traceThread = new Thread(TraceWorker)
                    {
                        IsBackground = true,
                        Name = "NetFix-DnsEtwWorker"
                    };
                    _traceThread.Start();
                    _isRunning = true;
                }
                finally
                {
                    Marshal.FreeHGlobal(propBuffer);
                }
            }
            catch
            {
                _isRunning = false;
            }
        }
    }

    private void TraceWorker()
    {
        var logfile = new EVENT_TRACE_LOGFILEW
        {
            LoggerName = SessionName,
            ProcessTraceMode = 0x00000100 | 0x10000000,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_eventRecordCallback!)
        };

        _traceHandle = OpenTraceW(ref logfile);
        if (_traceHandle != ulong.MaxValue && _traceHandle != 0)
        {
            ulong[] handles = [_traceHandle];
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void ProcessEventRecord(ref EVENT_RECORD record)
    {
        try
        {
            int pid = (int)record.EventHeader.ProcessId;
            int eventId = record.EventHeader.EventDescriptor.Id;

            if (record.UserData != IntPtr.Zero && record.UserDataLength > 0)
            {
                byte[] data = new byte[record.UserDataLength];
                Marshal.Copy(record.UserData, data, 0, record.UserDataLength);

                ParseDnsClientPayload(pid, eventId, data);
            }
        }
        catch
        {
        }
    }

    private void ParseDnsClientPayload(int pid, int eventId, byte[] data)
    {
        string text = Encoding.Unicode.GetString(data);
        var parts = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        string? queryName = null;
        List<string> resolvedIps = [];

        foreach (var part in parts)
        {
            string token = part.Trim();
            if (token.Length < 2) continue;

            if (token.Contains('.') && !IPAddress.TryParse(token, out _))
            {
                if (queryName is null && !token.Contains(';') && !token.Contains(' '))
                {
                    queryName = token;
                }
            }
            else if (token.Contains(';') || token.Contains(':') || token.Contains('.'))
            {
                var candidates = token.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var c in candidates)
                {
                    string cand = c.Trim();
                    if (IPAddress.TryParse(cand, out var ip))
                    {
                        resolvedIps.Add(ip.ToString());
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(queryName))
        {
            var recent = _pidRecentQueries.GetOrAdd(pid, _ => new());
            recent.Add(queryName);

            foreach (var ip in resolvedIps)
            {
                _pidIpToDomain[(pid, ip)] = queryName;
                _ipToDomain[ip] = queryName;
            }
        }
    }

    public string? GetDomainForEndpoint(int pid, string remoteIp)
    {
        if (_pidIpToDomain.TryGetValue((pid, remoteIp), out var domain))
        {
            return domain;
        }

        if (_ipToDomain.TryGetValue(remoteIp, out domain))
        {
            return domain;
        }

        return null;
    }

    public void RegisterManualMapping(string ip, string domain, int pid = 0)
    {
        _ipToDomain[ip] = domain;
        if (pid > 0)
        {
            _pidIpToDomain[(pid, ip)] = domain;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            try
            {
                StopExistingSession(SessionName);

                if (_traceHandle != ulong.MaxValue && _traceHandle != 0)
                {
                    CloseTrace(_traceHandle);
                    _traceHandle = ulong.MaxValue;
                }

                if (_traceThread is not null && _traceThread.IsAlive)
                {
                    _traceThread.Join(500);
                    _traceThread = null;
                }

                _pidIpToDomain.Clear();
                _ipToDomain.Clear();
                _pidRecentQueries.Clear();
            }
            catch
            {
            }
        }
    }

    private static void StopExistingSession(string name)
    {
        int propSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + (name.Length + 1) * 2 + 512;
        IntPtr propBuffer = Marshal.AllocHGlobal(propSize);
        try
        {
            for (int i = 0; i < propSize; i++) Marshal.WriteByte(propBuffer, i, 0);

            var prop = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER { BufferSize = (uint)propSize },
                LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
            };
            Marshal.StructureToPtr(prop, propBuffer, false);

            ControlTraceW(0, name, propBuffer, 1);
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(propBuffer);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }

    #region Win32 ETW P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENABLE_TRACE_PARAMETERS
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public uint SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(ref EVENT_RECORD eventRecord);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint control);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, ref ENABLE_TRACE_PARAMETERS enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    #endregion
}
