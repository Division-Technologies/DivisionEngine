// The slice of the CLR profiling API that this profiler needs, declared by hand.
//
// The official declarations (corprof.h) are MIDL output that pulls in windows.h and ole2.h, and
// building them outside Windows means vendoring the runtime's own PAL replacements for those. What
// is actually required to be a profiler is much smaller: a COM object whose vtable matches
// ICorProfilerCallback11, and the ability to call a handful of ICorProfilerInfo methods through
// theirs. So the types live here and the vtable layout - the part that must not be guessed - is
// generated from the runtime's headers into CorProfSlots.h.
//
// Like DivisionEngine/Profiling/TracyNative.cs, this is an ABI contract rather than an interface:
// a wrong slot index neither fails to build nor fails to load, it just calls the wrong function.

#pragma once

#include <cstdint>
#include <cstring>

#if defined(_WIN32)
#define DIVISION_STDCALL __stdcall
#define DIVISION_EXPORT extern "C" __declspec(dllexport)
#else
#define DIVISION_STDCALL
#define DIVISION_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace division::clr {

using Hresult = std::int32_t;
using Ulong = std::uint32_t;
using Dword = std::uint32_t;
using Bool32 = std::int32_t;
using Wchar = char16_t;
using ObjectId = std::uintptr_t;
using ModuleId = std::uintptr_t;
using FunctionId = std::uintptr_t;
using AssemblyId = std::uintptr_t;
using ThreadId = std::uintptr_t;
using MdToken = std::uint32_t;

/// The client id a FunctionIDMapper hands back, which the enter/leave hooks then receive in place
/// of the FunctionID. Ours is a pointer to an interned zone call site, so the hooks do no lookup.
using ClientId = std::uintptr_t;

/// CorOpenFlags::ofRead - metadata opened for reading only.
inline constexpr Dword kOpenForRead = 0;

inline constexpr Hresult kOk = 0;
inline constexpr Hresult kNoInterface = static_cast<Hresult>(0x80004002);
inline constexpr Hresult kNoAggregation = static_cast<Hresult>(0x80040110);
inline constexpr Hresult kClassNotAvailable = static_cast<Hresult>(0x80040111);
inline constexpr Hresult kFail = static_cast<Hresult>(0x80004005);
inline constexpr Hresult kInvalidArg = static_cast<Hresult>(0x80070057);

struct Guid {
    std::uint32_t Data1;
    std::uint16_t Data2;
    std::uint16_t Data3;
    std::uint8_t Data4[8];
};

inline bool operator==(const Guid& left, const Guid& right) {
    return std::memcmp(&left, &right, sizeof(Guid)) == 0;
}

inline constexpr Guid kIidUnknown = {0x00000000, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
inline constexpr Guid kIidClassFactory = {0x00000001, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};

/// The CLSID the runtime is pointed at with CORECLR_PROFILER. Ours alone; it identifies nothing else.
inline constexpr Guid kClsidDivisionClrProfiler = {0x9f2c6d1a, 0x4e7b, 0x4f3d, {0xb0, 0x5a, 0x1c, 0x7d, 0x8e, 0x94, 0x2a, 0x61}};

/// COR_PRF_MONITOR. Only the flags this profiler sets; see corprof.h for the rest.
enum Monitor : Dword {
    kMonitorNone = 0,
    kMonitorModuleLoads = 0x4,
    kMonitorJitCompilation = 0x20,
    /// Also what delivers the unwind callbacks, which is how a zone survives an exception.
    kMonitorExceptions = 0x40,
    kMonitorThreads = 0x200,
    /// Every managed call and return. Ruinous by design - it also turns inlining off. See deep mode.
    kMonitorEnterLeave = 0x1000,
    kMonitorSuspends = 0x10000,
};

/// COR_PRF_HIGH_MONITOR.
enum HighMonitor : Dword {
    kHighMonitorNone = 0,
    /// GarbageCollectionStarted/Finished without COR_PRF_MONITOR_GC's cost: the low-mask flag turns
    /// concurrent (background) GC off for the whole process, which would change what is being
    /// measured. This one leaves it on, at the price of the object-level GC callbacks we do not use.
    kHighBasicGc = 0x10,
};

/// COR_PRF_SUSPEND_REASON.
enum SuspendReason : Dword {
    kSuspendOther = 0,
    kSuspendForGc = 1,
    kSuspendForAppDomainShutdown = 2,
    kSuspendForCodePitching = 3,
    kSuspendForShutdown = 4,
    kSuspendForInprocDebugger = 6,
    kSuspendForGcPrep = 7,
    kSuspendForRejit = 8,
    kSuspendForProfiler = 9,
    kSuspendReasonCount = 10,
};

/// COR_PRF_GC_REASON.
enum GcReason : Dword {
    kGcOther = 0,
    kGcInduced = 1,
};

/// A vtable slot whose arguments this profiler never reads.
///
/// Declaring no parameters rather than a variadic list is deliberate: on Apple arm64 a variadic
/// callee expects its arguments on the stack while the caller passes them in registers, so a
/// variadic declaration would be the wrong ABI. A narrower fixed signature is safe under both
/// AAPCS and SysV, since neither requires the callee to know about arguments it ignores.
using UnusedSlot = Hresult(DIVISION_STDCALL*)(void* self);

}  // namespace division::clr
