using System;
using System.Runtime.InteropServices;
using System.Text; // Added for StringBuilder

namespace NetworkMonitorService
{
    internal static class NativeWfpApi
    {
        // --- Constants ---\
        public const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;

        // --- Enums (FWP_ACTION_TYPE, FWP_DATA_TYPE, FWP_MATCH_TYPE) ---
        // ... (Existing enums remain the same) ...
        public enum FWP_ACTION_TYPE : uint { BLOCK = 0x0001, PERMIT = 0x0002, CALLOUT_TERMINATING = 0x00000003 }
        public enum FWP_DATA_TYPE { FWP_EMPTY=0, UINT8 = 0, UINT16, UINT32, UINT64, INT8, INT16, INT32, INT64, FLOAT, DOUBLE, SID, SECURITY_DESCRIPTOR, TOKEN_INFORMATION, TOKEN_ACCESS_INFORMATION, UNICODE_STRING_TYPE, BYTE_BLOB_TYPE, RANGE_TYPE, MAX }
        public enum FWP_MATCH_TYPE { EQUAL=0, GREATER, LESS, GREATER_OR_EQUAL, LESS_OR_EQUAL, RANGE, FLAGS_ALL_SET, FLAGS_ANY_SET, FLAGS_NONE_SET, EQUAL_CASE_INSENSITIVE, NOT_EQUAL, PREFIX, NOT_PREFIX, MAX }
        [Flags] // Added for Filter Flags
        public enum FWPM_FILTER_FLAGS : uint
        {
            NONE = 0x00000000,
            PERSISTENT = 0x00000001,
            BOOTTIME = 0x00000002,
            HAS_PROVIDER_CONTEXT = 0x00000004,
            // ... other flags can be added if needed
        }


        // --- Layer GUIDs ---
        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new Guid("c38d57d0-0df4-4c0f-905a-c2e14ead3d33");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 = new Guid("d4beabff-0b6b-46cf-b856-a74540ff1df9");

        // --- Condition GUIDs ---
        public static readonly Guid FWPM_CONDITION_ALE_APP_ID = new Guid("b95fb905-58d4-4082-8889-597aaa1f0be9");

        // --- Added Sublayer GUID ---
        public static readonly Guid FWPM_SUBLAYER_UNIVERSAL = new Guid("00000000-0000-0000-0000-000000000000"); // Example, check if correct - often default is sufficient

        // --- Added Standard Block Callout GUID ---
        public static readonly Guid FWPM_CALLOUT_BLOCK = new Guid("948DA74A-2E7F-4D30-A6F6-94A77E2B3E54");

        // --- Added Filter Type GUID ---
        public static readonly Guid FWP_FILTER_TYPE_CONDITION = new Guid("{06C2CBE6-2A8B-4D8A-AD1E-C0A81158370A}");


        // --- Structures (FWP_BYTE_BLOB, FWP_VALUE0_UNION, FWP_VALUE0, FWPM_DISPLAY_DATA0, FWPM_ACTION0, FWPM_FILTER_CONDITION0, FWPM_FILTER0) ---
        // ... (Existing structures remain the same) ...
        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_BYTE_BLOB { public uint size; public IntPtr data; }

        [StructLayout(LayoutKind.Explicit)]
        public struct FWP_VALUE0_UNION
        {
            [FieldOffset(0)] public byte uint8;
            [FieldOffset(0)] public ushort uint16;
            [FieldOffset(0)] public uint uint32;
            [FieldOffset(0)] public ulong uint64;
            [FieldOffset(0)] public sbyte int8;
            [FieldOffset(0)] public short int16;
            [FieldOffset(0)] public int int32;
            [FieldOffset(0)] public long int64;
            [FieldOffset(0)] public float float32;
            [FieldOffset(0)] public double double64;
            [FieldOffset(0)] public IntPtr sid; // PSID
            [FieldOffset(0)] public IntPtr sd; // PSECURITY_DESCRIPTOR
            [FieldOffset(0)] public IntPtr tokenInformation; // PTOKEN_INFORMATION
            [FieldOffset(0)] public IntPtr tokenAccessInformation; // PTOKEN_ACCESS_INFORMATION
            [FieldOffset(0)] public IntPtr unicodeString; // PCWSTR
            [FieldOffset(0)] public IntPtr byteBlob; // Pointer to FWP_BYTE_BLOB
            // FWP_RANGE0, FWP_V4_ADDR_AND_MASK, FWP_V6_ADDR_AND_MASK etc. can be added if needed
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_VALUE0 { public FWP_DATA_TYPE type; public FWP_VALUE0_UNION value; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FWPM_DISPLAY_DATA0
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string name;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string description;
        }

        // Use Explicit layout to map the C union correctly
        [StructLayout(LayoutKind.Explicit)]
        public struct FWPM_ACTION0
        {
            [FieldOffset(0)]
            public FWP_ACTION_TYPE type;
            
            // Union members - occupy the same memory space after 'type'
            [FieldOffset(4)] // Assuming FWP_ACTION_TYPE (uint) is 4 bytes
            public Guid filterType; // For FWP_ACTION_TYPE.BLOCK with specific filter types
            
            [FieldOffset(4)] // Assuming FWP_ACTION_TYPE (uint) is 4 bytes
            public Guid calloutKey; // For FWP_ACTION_CALLOUT_TERMINATING/INSPECTION/UNKNOWN
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER_CONDITION0
        {
            public Guid fieldKey;
            public FWP_MATCH_TYPE matchType;
            public FWP_VALUE0 conditionValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER0
        {
            public Guid filterKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public FWPM_FILTER_FLAGS flags; // Changed from UINT32
            public IntPtr providerKey; // GUID* -> Use IntPtr or Guid? Use IntPtr for marshaling C pointer
            public FWP_BYTE_BLOB providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public FWP_VALUE0 weight;
            public uint numFilterConditions;
            public IntPtr filterCondition; // FWPM_FILTER_CONDITION0* -> Use IntPtr
            public FWPM_ACTION0 action;
            public ulong rawContext; // Or Guid providerContextKey based on flags
            // Explicitly define the union if needed, using StructLayout Kind.Explicit
            // public Guid providerContextKey; // If using the union part
            public IntPtr reserved; // GUID* -> Use IntPtr
            public ulong filterId; // Read-only
            public FWP_VALUE0 effectiveWeight; // Read-only
        }

        // --- Added Process Access Flags Enum ---
        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x000000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000, // Needed for QueryFullProcessImageName
            Synchronize = 0x00100000
        }


        // --- Function Signatures (DllImport) ---

        private const string FwpuclntDll = "Fwpuclnt.dll";
        private const string Kernel32Dll = "kernel32.dll";

        // WFP Functions (Existing)
        [DllImport(FwpuclntDll, EntryPoint="FwpmEngineOpen0", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Unicode)]
        public static extern uint FwpmEngineOpen(
            [In, Optional] string? serverName, // Allow null for local engine
            uint authnService, // Use RPC_C_AUTHN_WINNT or RPC_C_AUTHN_DEFAULT
            [In, Optional] IntPtr authIdentity, // Usually null
            [In, Optional] IntPtr session, // FWPM_SESSION0*, can be null
            out IntPtr engineHandle);

        [DllImport(FwpuclntDll, EntryPoint="FwpmEngineClose0", CallingConvention=CallingConvention.StdCall)]
        public static extern uint FwpmEngineClose(IntPtr engineHandle);

        [DllImport(FwpuclntDll, EntryPoint="FwpmFilterAdd0", CallingConvention=CallingConvention.StdCall)]
        public static extern uint FwpmFilterAdd(
            IntPtr engineHandle,
            IntPtr filter, // const FWPM_FILTER0* -> Use IntPtr, marshal manually
            [In, Optional] IntPtr sd, // PSECURITY_DESCRIPTOR, usually null
            out ulong id // Optional, UINT64*
        );

        [DllImport(FwpuclntDll, EntryPoint="FwpmFilterDeleteByKey0", CallingConvention=CallingConvention.StdCall)]
        public static extern uint FwpmFilterDeleteByKey(
            IntPtr engineHandle,
            [In] ref Guid key);

        [DllImport(FwpuclntDll, EntryPoint="FwpmGetAppIdFromFileName0", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Unicode)]
        public static extern uint FwpmGetAppIdFromFileName(
            string fileName,
            out IntPtr appId // FWP_BYTE_BLOB** -> returns allocated memory, needs FwpmFreeMemory
        );

        [DllImport(FwpuclntDll, EntryPoint="FwpmFreeMemory0", CallingConvention=CallingConvention.StdCall)]
        public static extern void FwpmFreeMemory(ref IntPtr p); // Takes PVOID*


        // --- Added Kernel32 Functions ---

        [DllImport(Kernel32Dll, SetLastError = true)]
        public static extern IntPtr OpenProcess(
           ProcessAccessFlags processAccess,
           bool bInheritHandle,
           int processId);

        [DllImport(Kernel32Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport(Kernel32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(
           IntPtr hProcess,
           int dwFlags, // 0 = Win32 path format
           [Out] StringBuilder lpExeName,
           ref int lpdwSize);
    }
}