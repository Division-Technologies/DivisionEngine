// A CLR profiler that draws runtime events as Tracy zones.
//
// The engine's own instrumentation can only see what the engine does. Stop-the-world suspensions,
// garbage collections and just-in-time compilation happen underneath it, on the runtime's schedule,
// and they are exactly what a capture cannot explain when every thread stalls at once. The CLR
// hands those out through its profiling API, which is an in-process COM object the runtime loads
// from CORECLR_PROFILER_PATH before any managed code runs, and whose callbacks arrive synchronously
// on the thread that raised the event - so a zone can simply be opened and closed around them.
//
// Nothing here is reachable from managed code and nothing links against the engine. The two sides
// meet only in the Tracy client: this library emits into the same shared DivisionTracy that
// DivisionEngine/Profiling/TracyNative.cs calls, which is why it must not start or stop that
// client. The engine owns its lifetime (TRACY_MANUAL_LIFETIME); we emit only once it is up, and a
// process whose engine never starts the profiler simply produces nothing here.
//
// See Notes/Core/Profiling.md for how to run with it.

#include <tracy/TracyC.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "CorProfSlots.h"

namespace division::clr {
namespace {

// ----- configuration -----

/// Event groups, selected by DIVISION_CLR_EVENTS. Separate because their volumes differ by orders
/// of magnitude: suspensions are rare and always interesting, JIT zones flood the first second.
enum Category : unsigned {
    kCategorySuspend = 1u << 0,
    kCategoryGc = 1u << 1,
    kCategoryJit = 1u << 2,
    kCategoryLoader = 1u << 3,
    kCategoryDeep = 1u << 4,
};

constexpr unsigned kDefaultCategories = kCategorySuspend | kCategoryGc | kCategoryJit;

struct CategoryName {
    const char* name;
    unsigned value;
};

constexpr CategoryName kCategoryNames[] = {
    {"suspend", kCategorySuspend},
    {"gc", kCategoryGc},
    {"jit", kCategoryJit},
    {"loader", kCategoryLoader},
    {"deep", kCategoryDeep},
    // Deliberately not in "all": deep mode costs an order of magnitude and has to be asked for.
    {"all", kCategorySuspend | kCategoryGc | kCategoryJit | kCategoryLoader},
    {"none", 0},
};

unsigned g_categories = kDefaultCategories;
bool g_verbose = false;

void Report(const char* format, ...) {
    if (!g_verbose) {
        return;
    }

    std::va_list args;
    va_start(args, format);
    std::fputs("[DivisionClrProfiler] ", stderr);
    std::vfprintf(stderr, format, args);
    std::fputc('\n', stderr);
    va_end(args);
}

/// Parses DIVISION_CLR_EVENTS, a comma-separated list of the names above. An unknown name is
/// reported and ignored rather than fatal: a profiler that refuses to load over a typo in an
/// environment variable is worse than one that records less than asked.
unsigned ParseCategories(const char* value) {
    if (value == nullptr || *value == '\0') {
        return kDefaultCategories;
    }

    unsigned categories = 0;
    const char* cursor = value;
    while (*cursor != '\0') {
        const char* end = std::strchr(cursor, ',');
        const std::size_t length = end == nullptr ? std::strlen(cursor) : static_cast<std::size_t>(end - cursor);

        bool matched = false;
        for (const auto& candidate : kCategoryNames) {
            if (std::strlen(candidate.name) == length && std::strncmp(candidate.name, cursor, length) == 0) {
                categories |= candidate.value;
                matched = true;
                break;
            }
        }

        if (!matched) {
            std::fprintf(stderr, "[DivisionClrProfiler] unknown event group in DIVISION_CLR_EVENTS: %.*s\n", static_cast<int>(length), cursor);
        }

        cursor = end == nullptr ? cursor + length : end + 1;
    }

    return categories;
}

// ----- zones -----

// Colors are chosen against DivisionEngine/Profiling/ProfilerColors.cs: the engine's own zones are
// blue (phase), orange (behavior) and muted grey (idle), so the runtime gets the red end. A
// stop-the-world is the one thing on the timeline that no engine-side change can schedule around.
constexpr std::uint32_t kColorSuspend = 0xB03A3A;
constexpr std::uint32_t kColorStopped = 0x7C1F1F;
constexpr std::uint32_t kColorGc = 0x8F4FBF;
constexpr std::uint32_t kColorJit = 0x3F8F6F;
constexpr std::uint32_t kColorLoader = 0x8F7F3F;
constexpr std::uint32_t kColorDeep = 0x4F6F8F;

/// A zone call site. The file and line are this file's, so the profiler's UI still lands on the
/// code that opened the zone - there is no managed call site to point at.
#define DIVISION_ZONE(name, color) \
    ___tracy_source_location_data { (name), "DivisionClrProfiler", __FILE__, __LINE__, (color) }

/// One source location per suspend reason, indexed by COR_PRF_SUSPEND_REASON. Distinct locations
/// rather than one zone renamed per event, because Tracy aggregates its statistics per call site:
/// sharing one would collapse "how long does the runtime stop for a GC" into the same row as
/// "how long for a rejit". Same reasoning as Profiler.ZoneNamed on the managed side.
constexpr ___tracy_source_location_data kSuspendLocations[kSuspendReasonCount] = {
    DIVISION_ZONE("EE suspend: other", kColorSuspend),
    DIVISION_ZONE("EE suspend: GC", kColorSuspend),
    DIVISION_ZONE("EE suspend: appdomain shutdown", kColorSuspend),
    DIVISION_ZONE("EE suspend: code pitching", kColorSuspend),
    DIVISION_ZONE("EE suspend: shutdown", kColorSuspend),
    DIVISION_ZONE("EE suspend: unused", kColorSuspend),
    DIVISION_ZONE("EE suspend: in-proc debugger", kColorSuspend),
    DIVISION_ZONE("EE suspend: GC prep", kColorSuspend),
    DIVISION_ZONE("EE suspend: rejit", kColorSuspend),
    DIVISION_ZONE("EE suspend: profiler", kColorSuspend),
};

constexpr ___tracy_source_location_data kStoppedLocation = DIVISION_ZONE("EE stopped", kColorStopped);
constexpr ___tracy_source_location_data kGcLocations[] = {
    DIVISION_ZONE("GC gen0", kColorGc),
    DIVISION_ZONE("GC gen1", kColorGc),
    DIVISION_ZONE("GC gen2", kColorGc),
};
constexpr ___tracy_source_location_data kGcUnknownLocation = DIVISION_ZONE("GC", kColorGc);
constexpr ___tracy_source_location_data kJitLocation = DIVISION_ZONE("JIT", kColorJit);
constexpr ___tracy_source_location_data kModuleLocation = DIVISION_ZONE("Module load", kColorLoader);

/// The zones this profiler holds open across two callbacks, as one stack per thread.
///
/// Tracy models a thread's zones as a stack: they must close in the order they opened. Runtime
/// events do not always oblige. A background collection starts inside a suspension and outlives it,
/// so on one thread "GC gen2" and "EE stopped" genuinely cross. Where that happens the crossing
/// zone is closed and reopened immediately, which splits it into abutting segments instead of
/// either losing it or corrupting the capture - and a capture with a single bad pair is rejected
/// whole ("Invalid order of zone begin and end events"), so this is not a case to leave to luck.
enum ZoneKey : unsigned {
    kKeySuspend,
    kKeyStopped,
    kKeyGc,
    kKeyJit,
    kKeyModule,
    kKeyDeep,
    kKeyCount,
};

struct OpenZone {
    TracyCZoneCtx context;
    const ___tracy_source_location_data* location;
    unsigned key;
    bool emitted;
};

/// How deep the stack may get. Ordinarily a handful of zones nest; deep mode nests one per managed
/// frame, so the limit doubles as the cap on how much of a call tree is recorded.
constexpr int kStackLimit = 32;
constexpr int kDeepStackLimitDefault = 64;

int g_stackLimit = kStackLimit;

/// How many nested managed frames deep mode records before it stops opening zones. A cap rather
/// than a fixed size: a recursive descent parser will happily nest a thousand frames, and the
/// interesting part is the top of that, not the bottom.
int DeepStackLimit() {
    const char* value = std::getenv("DIVISION_CLR_DEEP_DEPTH");
    if (value != nullptr) {
        const int parsed = std::atoi(value);
        if (parsed > 0) {
            return parsed;
        }
    }

    return kDeepStackLimitDefault;
}

/// Per thread, grown on demand. Not a fixed array: deep mode needs hundreds of entries and every
/// other mode needs a handful, and paying the deep size in thread-local storage on every thread of
/// a process that is not deep profiling would be the wrong trade.
struct ZoneStack {
    OpenZone* items = nullptr;
    int depth = 0;
    int capacity = 0;

    /// Zones that could not be opened because the stack was full, so that their close still balances.
    unsigned dropped[kKeyCount] = {};

    ~ZoneStack() {
        std::free(items);
    }

    bool Reserve(int wanted) {
        if (wanted <= capacity) {
            return true;
        }

        const int grown = capacity == 0 ? 16 : capacity * 2;
        const int size = grown > wanted ? grown : wanted;
        auto* moved = static_cast<OpenZone*>(std::realloc(items, sizeof(OpenZone) * static_cast<std::size_t>(size)));
        if (moved == nullptr) {
            return false;
        }

        items = moved;
        capacity = size;
        return true;
    }
};

thread_local ZoneStack t_zones;

std::atomic<std::uint32_t> g_splits{0};

constexpr char kContinued[] = "continued";

/// Whether the Tracy client is up. Emitting before that is undefined under TRACY_MANUAL_LIFETIME,
/// and the runtime starts calling us long before any managed code runs.
bool TracyReady() {
    return ___tracy_profiler_started() != 0;
}

/// Brings the Tracy client up from here rather than waiting for the engine to do it.
///
/// Needed for deep mode, which has to run against a build that has DIVISION_PROFILING off - and
/// such a build never starts the client, so without this there would be nothing to record into.
/// The managed side checks whether the client is already running before starting it, so the two
/// cannot both start it.
void StartTracyIfAsked() {
    const char* value = std::getenv("DIVISION_CLR_START_TRACY");
    if (value == nullptr || value[0] != '1' || ___tracy_profiler_started() != 0) {
        return;
    }

    ___tracy_startup_profiler();
    Report("started the Tracy client");
}

void Annotate(OpenZone* zone, const char* text, std::size_t length) {
    if (zone != nullptr && zone->emitted && TracyReady()) {
        ___tracy_emit_zone_text(zone->context, text, length);
    }
}

void OpenAt(OpenZone& zone) {
    zone.emitted = TracyReady();
    if (zone.emitted) {
        zone.context = ___tracy_emit_zone_begin(zone.location, 1);
    }
}

void CloseAt(OpenZone& zone) {
    if (zone.emitted && TracyReady()) {
        ___tracy_emit_zone_end(zone.context);
    }

    zone.emitted = false;
}

void PushZone(unsigned key, const ___tracy_source_location_data& location) {
    ZoneStack& stack = t_zones;
    if (stack.depth >= g_stackLimit || !stack.Reserve(stack.depth + 1)) {
        ++stack.dropped[key];
        return;
    }

    OpenZone& zone = stack.items[stack.depth++];
    zone.location = &location;
    zone.key = key;
    OpenAt(zone);
}

OpenZone* TopZone(unsigned key) {
    ZoneStack& stack = t_zones;
    for (int index = stack.depth - 1; index >= 0; --index) {
        if (stack.items[index].key == key) {
            return &stack.items[index];
        }
    }

    return nullptr;
}

void PopZone(unsigned key) {
    ZoneStack& stack = t_zones;
    if (stack.dropped[key] > 0) {
        --stack.dropped[key];
        return;
    }

    int target = -1;
    for (int index = stack.depth - 1; index >= 0; --index) {
        if (stack.items[index].key == key) {
            target = index;
            break;
        }
    }

    if (target < 0) {
        return;
    }

    for (int index = stack.depth - 1; index > target; --index) {
        CloseAt(stack.items[index]);
    }

    CloseAt(stack.items[target]);

    // Whatever was stacked on top of it is still running, so it starts again here as a new segment.
    for (int index = target + 1; index < stack.depth; ++index) {
        stack.items[index - 1] = stack.items[index];
        OpenAt(stack.items[index - 1]);
        Annotate(&stack.items[index - 1], kContinued, sizeof(kContinued) - 1);
        g_splits.fetch_add(1, std::memory_order_relaxed);
    }

    --stack.depth;
}

// ----- calling the runtime back -----

using QueryInterfaceFn = Hresult(DIVISION_STDCALL*)(void*, const Guid*, void**);
using ReleaseFn = Ulong(DIVISION_STDCALL*)(void*);
using SetEventMask2Fn = Hresult(DIVISION_STDCALL*)(void*, Dword, Dword);
using GetModuleInfoFn = Hresult(DIVISION_STDCALL*)(void*, ModuleId, const std::uint8_t**, Ulong, Ulong*, Wchar*, AssemblyId*);

/// The profiling API is COM without a COM runtime: an object is a pointer to a vtable pointer, and
/// a method is the slot at a known index. CorProfSlots.h supplies the indices.
template <typename Fn>
Fn Method(void* object, InfoSlot slot) {
    auto** vtable = *static_cast<void***>(object);
    return reinterpret_cast<Fn>(vtable[static_cast<std::size_t>(slot)]);
}

void* g_info = nullptr;

/// UTF-16 to UTF-8, for the wide strings the runtime returns. Lone surrogates are passed through as
/// U+FFFD rather than rejected: this is display text for a timeline, not a round trip.
std::string ToUtf8(const Wchar* text, std::size_t length) {
    std::string result;
    result.reserve(length);

    for (std::size_t index = 0; index < length; ++index) {
        char32_t code = text[index];
        if (code >= 0xD800 && code <= 0xDBFF && index + 1 < length && text[index + 1] >= 0xDC00 && text[index + 1] <= 0xDFFF) {
            code = 0x10000 + ((code - 0xD800) << 10) + (text[index + 1] - 0xDC00);
            ++index;
        } else if (code >= 0xD800 && code <= 0xDFFF) {
            code = 0xFFFD;
        }

        if (code < 0x80) {
            result.push_back(static_cast<char>(code));
        } else if (code < 0x800) {
            result.push_back(static_cast<char>(0xC0 | (code >> 6)));
            result.push_back(static_cast<char>(0x80 | (code & 0x3F)));
        } else if (code < 0x10000) {
            result.push_back(static_cast<char>(0xE0 | (code >> 12)));
            result.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
            result.push_back(static_cast<char>(0x80 | (code & 0x3F)));
        } else {
            result.push_back(static_cast<char>(0xF0 | (code >> 18)));
            result.push_back(static_cast<char>(0x80 | ((code >> 12) & 0x3F)));
            result.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
            result.push_back(static_cast<char>(0x80 | (code & 0x3F)));
        }
    }

    return result;
}

std::string ModuleName(ModuleId moduleId) {
    if (g_info == nullptr) {
        return {};
    }

    auto getModuleInfo = Method<GetModuleInfoFn>(g_info, InfoSlot::GetModuleInfo);
    Wchar buffer[512];
    Ulong written = 0;
    if (getModuleInfo(g_info, moduleId, nullptr, static_cast<Ulong>(sizeof(buffer) / sizeof(buffer[0])), &written, buffer, nullptr) != kOk || written == 0) {
        return {};
    }

    // The count includes the terminator.
    return ToUtf8(buffer, written - 1);
}

// ----- deep mode -----

/// A zone for every managed call, the equivalent of Unity's deep profiling.
///
/// The cost is not incidental, it is the feature: COR_PRF_MONITOR_ENTERLEAVE turns inlining off for
/// the whole process and wraps every call and return in a stub. Expect an order of magnitude, and a
/// capture measured in millions of zones per second. It answers a question the other modes cannot -
/// what, exactly, is this frame doing - and it is not a mode to leave on, which is why it is not in
/// the "all" group.
///
/// Three things here were settled by experiment on macOS arm64 with .NET 10, because the API offers
/// several ways to do this and only one of them works:
///
///  - `SetEnterLeaveFunctionHooks3` registers and the hooks are called, but the process corrupts
///    its own heap and dies (an AccessViolation somewhere unrelated, at shutdown or before).
///  - `SetEnterLeaveFunctionHooks3WithInfo` is refused outright with 0x80131374.
///  - `SetEnterLeaveFunctionHooks2` works: 1.5 million hook calls and a clean exit.
///
/// And installing a `FunctionIDMapper` alongside any of them crashes immediately after the mapper's
/// first call, whatever it returns - even the identity. So the client-id trick, which would have
/// let the hooks receive the interned call site directly and do no lookup at all, is off the table
/// here; names are resolved from the FunctionID instead.

using GetFunctionInfoFn = Hresult(DIVISION_STDCALL*)(void*, FunctionId, std::uintptr_t*, ModuleId*, MdToken*);
using GetModuleMetaDataFn = Hresult(DIVISION_STDCALL*)(void*, ModuleId, Dword, const Guid*, void**);
using GetMethodPropsFn = Hresult(DIVISION_STDCALL*)(void*, MdToken, MdToken*, Wchar*, Ulong, Ulong*, Dword*, const void**, Ulong*, Ulong*, Dword*);
using GetTypeDefPropsFn = Hresult(DIVISION_STDCALL*)(void*, MdToken, Wchar*, Ulong, Ulong*, Dword*, MdToken*);
using SetEnterLeaveHooks2Fn = Hresult(DIVISION_STDCALL*)(void*, void*, void*, void*);

constexpr ___tracy_source_location_data kDeepUnknownLocation = DIVISION_ZONE("managed", kColorDeep);

/// Stands for "this method is deliberately not instrumented". Its address is a sentinel in the
/// name table; the zone itself is never emitted.
constexpr ___tracy_source_location_data kDeepExcludedLocation = DIVISION_ZONE("excluded", 0);
constexpr ___tracy_source_location_data kDynamicMethodLocation = DIVISION_ZONE("dynamic method", kColorDeep);

/// Methods that must not be hooked, as "Type.Method" prefixes.
///
/// A deep zone opens when a method is entered and closes when it returns, so it can only nest
/// inside anything already open. A method that opens a Tracy zone and leaves it open for its caller
/// to close - which is exactly what Profiler.Zone does - breaks that: by the time it returns, the
/// zone it opened sits above its own, and closing its own is then out of order. One bad pair
/// invalidates the whole capture.
///
/// So the rule is: any method whose net effect on the zone stack is not zero has to be listed here.
/// In the engine that is the profiling API itself, which is the default. Override with
/// DIVISION_CLR_DEEP_EXCLUDE, a comma-separated list of prefixes; an empty value excludes nothing.
constexpr const char* kDefaultDeepExclude = "DivisionEngine.Profiler.,DivisionEngine.ProfilerZone.";

std::vector<std::string> g_deepExclude;

void LoadDeepExclusions() {
    const char* value = std::getenv("DIVISION_CLR_DEEP_EXCLUDE");
    const char* list = value != nullptr ? value : kDefaultDeepExclude;

    const char* cursor = list;
    while (*cursor != '\0') {
        const char* end = std::strchr(cursor, ',');
        const std::size_t length = end == nullptr ? std::strlen(cursor) : static_cast<std::size_t>(end - cursor);
        if (length > 0) {
            g_deepExclude.emplace_back(cursor, length);
        }

        cursor = end == nullptr ? cursor + length : end + 1;
    }
}

bool IsExcluded(const std::string& name) {
    for (const auto& prefix : g_deepExclude) {
        if (name.compare(0, prefix.size(), prefix) == 0) {
            return true;
        }
    }

    return false;
}

/// FunctionID to call site, written once per method and then read on every call.
///
/// Open addressing with atomic slots rather than a map behind a lock: the write side is the JIT,
/// which is rare, and the read side is every managed call on every thread, which must not contend.
/// A method whose name never arrives, and anything past a full table, falls back to "managed".
class FunctionNames {
  public:
    static constexpr std::size_t kCapacity = 1u << 16;

    const ___tracy_source_location_data* Find(FunctionId id) const {
        for (std::size_t probe = 0; probe < kMaxProbe; ++probe) {
            const Slot& slot = slots_[Index(id, probe)];
            const auto key = slot.key.load(std::memory_order_acquire);
            if (key == 0) {
                return nullptr;
            }

            if (key == id) {
                return slot.location.load(std::memory_order_relaxed);
            }
        }

        return nullptr;
    }

    void Add(FunctionId id, const ___tracy_source_location_data* location) {
        std::lock_guard<std::mutex> guard(mutex_);
        for (std::size_t probe = 0; probe < kMaxProbe; ++probe) {
            Slot& slot = slots_[Index(id, probe)];
            const auto key = slot.key.load(std::memory_order_relaxed);
            if (key == id) {
                return;
            }

            if (key == 0) {
                // The location is published before the key, so a reader that sees the key sees a
                // complete entry.
                slot.location.store(location, std::memory_order_relaxed);
                slot.key.store(id, std::memory_order_release);
                return;
            }
        }
    }

  private:
    struct Slot {
        std::atomic<FunctionId> key{0};
        std::atomic<const ___tracy_source_location_data*> location{nullptr};
    };

    static constexpr std::size_t kMaxProbe = 32;

    static std::size_t Index(FunctionId id, std::size_t probe) {
        // FunctionIDs are pointers; the low bits are alignment, so mix before masking.
        const auto hash = static_cast<std::size_t>((id >> 4) * 0x9E3779B97F4A7C15ull);
        return (hash + probe) & (kCapacity - 1);
    }

    mutable Slot slots_[kCapacity];
    std::mutex mutex_;
};

FunctionNames& Names() {
    static FunctionNames names;
    return names;
}

/// Copies a string where the profiler can read it whenever it likes. Never freed: one per method,
/// bounded by the methods the process compiles.
const char* DupUtf8(const std::string& value) {
    auto* buffer = static_cast<char*>(std::malloc(value.size() + 1));
    if (buffer != nullptr) {
        std::memcpy(buffer, value.c_str(), value.size() + 1);
    }

    return buffer;
}

/// "Type.Method", or empty if the metadata could not be read.
std::string MethodName(FunctionId functionId) {
    if (g_info == nullptr) {
        return {};
    }

    std::uintptr_t classId = 0;
    ModuleId moduleId = 0;
    MdToken token = 0;
    if (Method<GetFunctionInfoFn>(g_info, InfoSlot::GetFunctionInfo)(g_info, functionId, &classId, &moduleId, &token) != kOk) {
        return {};
    }

    void* import = nullptr;
    if (Method<GetModuleMetaDataFn>(g_info, InfoSlot::GetModuleMetaData)(g_info, moduleId, kOpenForRead, &kIidMetaDataImport, &import) != kOk || import == nullptr) {
        return {};
    }

    auto** metadata = *static_cast<void***>(import);
    std::string result;
    Wchar method[512];
    Ulong methodLength = 0;
    MdToken typeToken = 0;
    auto getMethodProps = reinterpret_cast<GetMethodPropsFn>(metadata[static_cast<std::size_t>(MetadataSlot::GetMethodProps)]);
    if (getMethodProps(import, token, &typeToken, method, static_cast<Ulong>(sizeof(method) / sizeof(method[0])), &methodLength, nullptr, nullptr, nullptr, nullptr, nullptr) == kOk && methodLength > 0) {
        Wchar type[512];
        Ulong typeLength = 0;
        auto getTypeDefProps = reinterpret_cast<GetTypeDefPropsFn>(metadata[static_cast<std::size_t>(MetadataSlot::GetTypeDefProps)]);
        if (getTypeDefProps(import, typeToken, type, static_cast<Ulong>(sizeof(type) / sizeof(type[0])), &typeLength, nullptr, nullptr) == kOk && typeLength > 0) {
            result = ToUtf8(type, typeLength - 1);
            result += '.';
        }

        result += ToUtf8(method, methodLength - 1);
    }

    Method<ReleaseFn>(import, InfoSlot::Release)(import);
    return result;
}

/// Records what a freshly compiled method is called.
///
/// Every method the hooks ever see passes through here first: enter and leave stubs are emitted by
/// the JIT, so code that was compiled ahead of time (most of the framework, as ReadyToRun) carries
/// no hooks at all and never reaches them. Which also means deep mode shows the engine and the user
/// code rather than the insides of the base class library.
void RememberName(FunctionId functionId) {
    if (Names().Find(functionId) != nullptr) {
        return;
    }

    // Recorded either way, including when the name cannot be read. Presence in the table is what
    // "this method carries hooks" means, and the enter, leave and unwind paths all rely on that
    // being the same answer - a method that is pushed but not popped is as bad as the reverse.
    const std::string name = MethodName(functionId);
    if (name.empty()) {
        Names().Add(functionId, &kDeepUnknownLocation);
        return;
    }

    if (IsExcluded(name)) {
        Names().Add(functionId, &kDeepExcludedLocation);
        return;
    }

    const char* copied = DupUtf8(name);
    auto* location = copied == nullptr
                         ? nullptr
                         : static_cast<___tracy_source_location_data*>(std::malloc(sizeof(___tracy_source_location_data)));
    if (location == nullptr) {
        Names().Add(functionId, &kDeepUnknownLocation);
        return;
    }

    location->name = copied;
    location->function = copied;
    location->file = __FILE__;
    location->line = 0;
    location->color = kColorDeep;
    Names().Add(functionId, location);
}

/// Whether this function's frames carry a zone. Unknown means it was never compiled while we were
/// attached - code that was compiled ahead of time has no hooks - and excluded means it is one of
/// the methods that must not be wrapped.
bool IsInstrumented(FunctionId functionId, const ___tracy_source_location_data*& location) {
    location = Names().Find(functionId);
    return location != nullptr && location != &kDeepExcludedLocation;
}

void DIVISION_STDCALL DeepEnter(FunctionId functionId, std::uintptr_t clientData, std::uintptr_t frameInfo, void* argumentInfo) {
    (void)clientData;
    (void)frameInfo;
    (void)argumentInfo;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(functionId, location)) {
        PushZone(kKeyDeep, *location);
    }
}

void DIVISION_STDCALL DeepLeave(FunctionId functionId, std::uintptr_t clientData, std::uintptr_t frameInfo, void* returnRange) {
    (void)clientData;
    (void)frameInfo;
    (void)returnRange;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(functionId, location)) {
        PopZone(kKeyDeep);
    }
}

/// A tail call replaces this frame with the callee's, so no leave will arrive for it. The callee
/// opens its own zone on entry and closes it on its own leave, which keeps the stack balanced.
void DIVISION_STDCALL DeepTailcall(FunctionId functionId, std::uintptr_t clientData, std::uintptr_t frameInfo) {
    (void)clientData;
    (void)frameInfo;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(functionId, location)) {
        PopZone(kKeyDeep);
    }
}

// ----- callbacks -----

/// Every slot this profiler does not implement. S_OK rather than E_NOTIMPL: the runtime treats a
/// failing callback as the profiler's problem, and there is nothing to report - we simply do not
/// subscribe to that event.
Hresult DIVISION_STDCALL Ignored(void*) {
    return kOk;
}

Hresult DIVISION_STDCALL Initialize(void* self, void* infoUnknown) {
    (void)self;

    auto queryInterface = Method<QueryInterfaceFn>(infoUnknown, InfoSlot::QueryInterface);
    // ICorProfilerInfo5 is the first with SetEventMask2, and the high mask is what lets the GC
    // callbacks be requested without COR_PRF_MONITOR_GC and its loss of concurrent GC.
    if (queryInterface(infoUnknown, &kIidCorProfilerInfo5, &g_info) != kOk || g_info == nullptr) {
        std::fprintf(stderr, "[DivisionClrProfiler] the runtime does not offer ICorProfilerInfo5; not recording.\n");
        return kFail;
    }

    Dword low = 0;
    Dword high = 0;
    if ((g_categories & kCategorySuspend) != 0) {
        low |= kMonitorSuspends;
    }
    const bool deep = (g_categories & kCategoryDeep) != 0;
    if ((g_categories & kCategoryJit) != 0 || deep) {
        // Deep mode subscribes to compilation for the names, not for the zones.
        low |= kMonitorJitCompilation;
    }
    if ((g_categories & kCategoryLoader) != 0) {
        low |= kMonitorModuleLoads;
    }
    if ((g_categories & kCategoryGc) != 0) {
        high |= kHighBasicGc;
    }

    if (deep) {
        // Exceptions come with it: without the unwind callbacks, the first exception leaves a zone
        // open on its thread and the capture is no longer properly nested.
        low |= kMonitorEnterLeave | kMonitorExceptions;
        g_stackLimit = DeepStackLimit();
        LoadDeepExclusions();
    }

    auto setEventMask2 = Method<SetEventMask2Fn>(g_info, InfoSlot::SetEventMask2);
    const Hresult status = setEventMask2(g_info, low, high);
    if (status != kOk) {
        std::fprintf(stderr, "[DivisionClrProfiler] SetEventMask2(0x%x, 0x%x) failed with 0x%08x; not recording.\n", low, high, static_cast<unsigned>(status));
        return status;
    }

    if (deep) {
        // The mapper has to be in place before the hooks are, or the first calls arrive carrying
        // raw FunctionIDs where the hooks expect a call site.
        const Hresult hooks = Method<SetEnterLeaveHooks2Fn>(g_info, InfoSlot::SetEnterLeaveFunctionHooks2)(
            g_info, reinterpret_cast<void*>(&DeepEnter), reinterpret_cast<void*>(&DeepLeave), reinterpret_cast<void*>(&DeepTailcall)
        );
        if (hooks != kOk) {
            std::fprintf(stderr, "[DivisionClrProfiler] deep mode unavailable (0x%08x).\n", static_cast<unsigned>(hooks));
        } else {
            Report("deep mode on, stack limit %d, %zu exclusion prefixes", g_stackLimit, g_deepExclude.size());
        }
    }

    StartTracyIfAsked();
    Report("attached; events low=0x%x high=0x%x", low, high);
    return kOk;
}

Hresult DIVISION_STDCALL Shutdown(void* self) {
    (void)self;

    if (g_info != nullptr) {
        Method<ReleaseFn>(g_info, InfoSlot::Release)(g_info);
        g_info = nullptr;
    }

    const std::uint32_t splits = g_splits.load(std::memory_order_relaxed);
    if (splits != 0) {
        Report("%u zones were split because the runtime's events crossed", splits);
    }

    return kOk;
}

Hresult DIVISION_STDCALL RuntimeSuspendStarted(void* self, Dword reason) {
    (void)self;
    PushZone(kKeySuspend, kSuspendLocations[reason < kSuspendReasonCount ? reason : kSuspendOther]);
    return kOk;
}

/// All threads are now parked. The inner zone separates "getting everyone to stop" from "everyone
/// is stopped", which is the difference between a thread that will not reach a safe point and a
/// genuinely long pause.
Hresult DIVISION_STDCALL RuntimeSuspendFinished(void* self) {
    (void)self;
    PushZone(kKeyStopped, kStoppedLocation);
    return kOk;
}

Hresult DIVISION_STDCALL RuntimeSuspendAborted(void* self) {
    (void)self;
    PopZone(kKeyStopped);
    PopZone(kKeySuspend);
    return kOk;
}

Hresult DIVISION_STDCALL RuntimeResumeStarted(void* self) {
    (void)self;
    PopZone(kKeyStopped);
    return kOk;
}

Hresult DIVISION_STDCALL RuntimeResumeFinished(void* self) {
    (void)self;
    PopZone(kKeySuspend);
    return kOk;
}

Hresult DIVISION_STDCALL GarbageCollectionStarted(void* self, int generationCount, Bool32* generationCollected, Dword reason) {
    (void)self;

    // The array is indexed by COR_PRF_GC_GENERATION, where 3 and 4 are the large and pinned object
    // heaps. The generation a capture is read by is the highest ephemeral one.
    int generation = -1;
    for (int index = 0; index < generationCount && index < 3; ++index) {
        if (generationCollected[index] != 0) {
            generation = index;
        }
    }

    PushZone(kKeyGc, generation >= 0 ? kGcLocations[generation] : kGcUnknownLocation);
    if (reason == kGcInduced) {
        Annotate(TopZone(kKeyGc), "induced", 7);
    }

    return kOk;
}

Hresult DIVISION_STDCALL GarbageCollectionFinished(void* self) {
    (void)self;
    PopZone(kKeyGc);
    return kOk;
}

Hresult DIVISION_STDCALL JitCompilationStarted(void* self, FunctionId functionId, Bool32 safeToBlock) {
    (void)self;
    (void)safeToBlock;

    if ((g_categories & kCategoryJit) == 0) {
        return kOk;
    }

    PushZone(kKeyJit, kJitLocation);
    if (OpenZone* zone = TopZone(kKeyJit); zone != nullptr && zone->emitted && TracyReady()) {
        // The method's name would need IMetaDataImport, which is a second ABI to pin down. The id
        // is enough to tell one method's compilations apart and to count them.
        ___tracy_emit_zone_value(zone->context, static_cast<std::uint64_t>(functionId));
    }

    return kOk;
}

Hresult DIVISION_STDCALL JitCompilationFinished(void* self, FunctionId functionId, Hresult status, Bool32 safeToBlock) {
    (void)self;
    (void)safeToBlock;

    // The method now exists, so its metadata can be read - which is the documented place to do it,
    // and the reason deep mode does not resolve names from inside the hooks.
    if ((g_categories & kCategoryDeep) != 0 && status == kOk) {
        RememberName(functionId);
    }

    if ((g_categories & kCategoryJit) != 0) {
        PopZone(kKeyJit);
    }

    return kOk;
}

/// IL stubs, lambdas compiled through DynamicMethod, and the like. They are JIT compiled and so do
/// carry hooks, but they have no metadata to name them - they are recorded under one shared name so
/// that entering and leaving them stays consistent.
Hresult DIVISION_STDCALL DynamicMethodJitCompilationFinished(void* self, FunctionId functionId, Hresult status, Bool32 safeToBlock) {
    (void)self;
    (void)safeToBlock;

    if ((g_categories & kCategoryDeep) != 0 && status == kOk && Names().Find(functionId) == nullptr) {
        Names().Add(functionId, &kDynamicMethodLocation);
    }

    return kOk;
}

Hresult DIVISION_STDCALL ModuleLoadStarted(void* self, ModuleId moduleId) {
    (void)self;
    (void)moduleId;

    PushZone(kKeyModule, kModuleLocation);
    return kOk;
}

Hresult DIVISION_STDCALL ModuleLoadFinished(void* self, ModuleId moduleId, Hresult status) {
    (void)self;
    (void)status;

    // Named here rather than at the start: the module has no name until it has been loaded.
    if (OpenZone* zone = TopZone(kKeyModule); zone != nullptr && zone->emitted) {
        const std::string name = ModuleName(moduleId);
        if (!name.empty()) {
            Annotate(zone, name.c_str(), name.size());
        }
    }

    PopZone(kKeyModule);
    return kOk;
}

/// An exception unwinding a frame skips its leave hook entirely, which would leave the zone open
/// and every later zone on the thread nested inside it. One pop per unwound frame restores that.
///
/// The enter half of the pair rather than the leave half, because only this one is told which
/// function is being unwound - and frames without hooks are unwound too, so popping blindly would
/// close zones belonging to somebody else.
Hresult DIVISION_STDCALL ExceptionUnwindFunctionEnter(void* self, FunctionId functionId) {
    (void)self;

    const ___tracy_source_location_data* location = nullptr;
    if ((g_categories & kCategoryDeep) != 0 && IsInstrumented(functionId, location)) {
        PopZone(kKeyDeep);
    }

    return kOk;
}

// ----- the COM object -----

struct CallbackVtable {
    void* slots[static_cast<std::size_t>(CallbackSlot::SlotCount)];
};

struct CallbackObject {
    const CallbackVtable* vtable;
};

Hresult DIVISION_STDCALL CallbackQueryInterface(void* self, const Guid* iid, void** result);
Ulong DIVISION_STDCALL CallbackAddRef(void*);
Ulong DIVISION_STDCALL CallbackRelease(void*);

template <typename Fn>
void Put(CallbackVtable& table, CallbackSlot slot, Fn function) {
    table.slots[static_cast<std::size_t>(slot)] = reinterpret_cast<void*>(function);
}

const CallbackVtable& Vtable() {
    static const CallbackVtable table = [] {
        CallbackVtable built{};
        for (auto& slot : built.slots) {
            slot = reinterpret_cast<void*>(&Ignored);
        }

        Put(built, CallbackSlot::QueryInterface, &CallbackQueryInterface);
        Put(built, CallbackSlot::AddRef, &CallbackAddRef);
        Put(built, CallbackSlot::Release, &CallbackRelease);
        Put(built, CallbackSlot::Initialize, &Initialize);
        Put(built, CallbackSlot::Shutdown, &Shutdown);
        Put(built, CallbackSlot::RuntimeSuspendStarted, &RuntimeSuspendStarted);
        Put(built, CallbackSlot::RuntimeSuspendFinished, &RuntimeSuspendFinished);
        Put(built, CallbackSlot::RuntimeSuspendAborted, &RuntimeSuspendAborted);
        Put(built, CallbackSlot::RuntimeResumeStarted, &RuntimeResumeStarted);
        Put(built, CallbackSlot::RuntimeResumeFinished, &RuntimeResumeFinished);
        Put(built, CallbackSlot::GarbageCollectionStarted, &GarbageCollectionStarted);
        Put(built, CallbackSlot::GarbageCollectionFinished, &GarbageCollectionFinished);
        Put(built, CallbackSlot::JITCompilationStarted, &JitCompilationStarted);
        Put(built, CallbackSlot::JITCompilationFinished, &JitCompilationFinished);
        Put(built, CallbackSlot::ModuleLoadStarted, &ModuleLoadStarted);
        Put(built, CallbackSlot::ModuleLoadFinished, &ModuleLoadFinished);
        Put(built, CallbackSlot::DynamicMethodJITCompilationFinished, &DynamicMethodJitCompilationFinished);
        Put(built, CallbackSlot::ExceptionUnwindFunctionEnter, &ExceptionUnwindFunctionEnter);
        return built;
    }();

    return table;
}

CallbackObject& Callback() {
    static CallbackObject object{&Vtable()};
    return object;
}

/// The runtime asks for the highest callback interface it knows and calls only what that answer
/// allows, so every version is accepted: all 98 slots exist, and the ones not implemented above
/// are the shared do-nothing.
bool IsCallbackInterface(const Guid& iid) {
    return iid == kIidUnknown || iid == kIidCorProfilerCallback || iid == kIidCorProfilerCallback2 || iid == kIidCorProfilerCallback3 ||
           iid == kIidCorProfilerCallback4 || iid == kIidCorProfilerCallback5 || iid == kIidCorProfilerCallback6 ||
           iid == kIidCorProfilerCallback7 || iid == kIidCorProfilerCallback8 || iid == kIidCorProfilerCallback9 ||
           iid == kIidCorProfilerCallback10 || iid == kIidCorProfilerCallback11;
}

Hresult DIVISION_STDCALL CallbackQueryInterface(void* self, const Guid* iid, void** result) {
    if (iid == nullptr || result == nullptr) {
        return kInvalidArg;
    }

    if (!IsCallbackInterface(*iid)) {
        *result = nullptr;
        return kNoInterface;
    }

    *result = self;
    return kOk;
}

// The object is a singleton that lives as long as the library, so the count exists only to satisfy
// callers that check it. There is nothing to free.
Ulong DIVISION_STDCALL CallbackAddRef(void*) {
    return 2;
}

Ulong DIVISION_STDCALL CallbackRelease(void*) {
    return 1;
}

// ----- the class factory -----

struct FactoryVtable {
    void* slots[5];
};

struct FactoryObject {
    const FactoryVtable* vtable;
};

Hresult DIVISION_STDCALL FactoryQueryInterface(void* self, const Guid* iid, void** result) {
    if (iid == nullptr || result == nullptr) {
        return kInvalidArg;
    }

    if (!(*iid == kIidUnknown) && !(*iid == kIidClassFactory)) {
        *result = nullptr;
        return kNoInterface;
    }

    *result = self;
    return kOk;
}

Ulong DIVISION_STDCALL FactoryAddRef(void*) {
    return 2;
}

Ulong DIVISION_STDCALL FactoryRelease(void*) {
    return 1;
}

Hresult DIVISION_STDCALL FactoryCreateInstance(void*, void* outer, const Guid* iid, void** result) {
    if (outer != nullptr) {
        return kNoAggregation;
    }

    return CallbackQueryInterface(&Callback(), iid, result);
}

Hresult DIVISION_STDCALL FactoryLockServer(void*, Bool32) {
    return kOk;
}

FactoryObject& Factory() {
    static const FactoryVtable table = {{
        reinterpret_cast<void*>(&FactoryQueryInterface),
        reinterpret_cast<void*>(&FactoryAddRef),
        reinterpret_cast<void*>(&FactoryRelease),
        reinterpret_cast<void*>(&FactoryCreateInstance),
        reinterpret_cast<void*>(&FactoryLockServer),
    }};
    static FactoryObject object{&table};
    return object;
}

/// Runs when the runtime loads the library, before Initialize. Only reads configuration: the Tracy
/// client belongs to the engine and is not touched from here.
struct Configuration {
    Configuration() {
        const char* verbose = std::getenv("DIVISION_CLR_VERBOSE");
        g_verbose = verbose != nullptr && verbose[0] == '1';
        g_categories = ParseCategories(std::getenv("DIVISION_CLR_EVENTS"));
    }
};

[[maybe_unused]] const Configuration g_configuration;

}  // namespace
}  // namespace division::clr

/// The entry point the runtime resolves out of CORECLR_PROFILER_PATH. On every platform it is a
/// plain exported symbol, looked up by name rather than through any COM registry.
DIVISION_EXPORT division::clr::Hresult DIVISION_STDCALL DllGetClassObject(const division::clr::Guid* clsid, const division::clr::Guid* iid, void** result) {
    using namespace division::clr;

    if (clsid == nullptr || iid == nullptr || result == nullptr) {
        return kInvalidArg;
    }

    if (!(*clsid == kClsidDivisionClrProfiler)) {
        *result = nullptr;
        return kClassNotAvailable;
    }

    return FactoryQueryInterface(&Factory(), iid, result);
}

DIVISION_EXPORT division::clr::Hresult DIVISION_STDCALL DllCanUnloadNow() {
    // S_FALSE: the profiler stays for the life of the process.
    return 1;
}

#undef DIVISION_ZONE
