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

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <format>
#include <mutex>
#include <new>
#include <optional>
#include <source_location>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "CorProfSlots.h"

namespace division::clr {
namespace {

// ----- configuration -----

/// An environment variable, or nothing if it is unset. MSVC's CRT deprecates std::getenv, so
/// Windows goes through getenv_s, which copies the value out instead of handing back its storage.
std::optional<std::string> ReadEnvironment(const char* name) {
#if defined(_WIN32)
    std::size_t required = 0;
    if (getenv_s(&required, nullptr, 0, name) != 0 || required == 0) {
        return std::nullopt;
    }

    std::string value(required, '\0');
    if (getenv_s(&required, value.data(), value.size(), name) != 0) {
        return std::nullopt;
    }

    // The count includes the terminator.
    value.resize(required - 1);
    return value;
#else
    const char* value = std::getenv(name);
    if (value == nullptr) {
        return std::nullopt;
    }

    return std::string(value);
#endif
}

/// Calls visit with each entry of a comma-separated list. A trailing comma adds no empty entry.
template <typename Visit>
void ForEachListEntry(std::string_view list, Visit visit) {
    while (!list.empty()) {
        const std::size_t comma = list.find(',');
        visit(list.substr(0, comma));
        if (comma == std::string_view::npos) {
            return;
        }

        list.remove_prefix(comma + 1);
    }
}

/// Event groups, selected by DIVISION_CLR_EVENTS. Separate because their volumes differ by orders
/// of magnitude: suspensions are rare and always interesting, JIT zones flood the first second.
enum Category : std::uint8_t {
    kCategorySuspend = 1u << 0,
    kCategoryGc = 1u << 1,
    kCategoryJit = 1u << 2,
    kCategoryLoader = 1u << 3,
    kCategoryDeep = 1u << 4,
};

constexpr unsigned DEFAULT_CATEGORIES = kCategorySuspend | kCategoryGc | kCategoryJit;

struct CategoryName {
    std::string_view name;
    unsigned value;
};

constexpr std::array<CategoryName, 7> CATEGORY_NAMES = {{
    {"suspend", kCategorySuspend},
    {"gc", kCategoryGc},
    {"jit", kCategoryJit},
    {"loader", kCategoryLoader},
    {"deep", kCategoryDeep},
    // Deliberately not in "all": deep mode costs an order of magnitude and has to be asked for.
    {"all", kCategorySuspend | kCategoryGc | kCategoryJit | kCategoryLoader},
    {"none", 0},
}};

unsigned g_categories = DEFAULT_CATEGORIES;
bool g_verbose = false;

/// Writes one line to stderr. Always, unlike Report: for what the user has to know about, such as
/// asking for something this profiler cannot do.
template <typename... Args>
void Warn(std::format_string<Args...> format, Args&&... args) {
    const std::string line = std::format("[DivisionClrProfiler] {}\n", std::format(format, std::forward<Args>(args)...));
    std::fputs(line.c_str(), stderr);
}

/// Writes one line to stderr when DIVISION_CLR_VERBOSE is set.
template <typename... Args>
void Report(std::format_string<Args...> format, Args&&... args) {
    if (g_verbose) {
        Warn(format, std::forward<Args>(args)...);
    }
}

/// Parses DIVISION_CLR_EVENTS, a comma-separated list of the names above. An unknown name is
/// reported and ignored rather than fatal: a profiler that refuses to load over a typo in an
/// environment variable is worse than one that records less than asked.
unsigned ParseCategories(const std::optional<std::string>& value) {
    if (!value.has_value() || value->empty()) {
        return DEFAULT_CATEGORIES;
    }

    unsigned categories = 0;
    ForEachListEntry(*value, [&](std::string_view entry) {
        const auto candidate = std::ranges::find(CATEGORY_NAMES, entry, &CategoryName::name);
        if (candidate != CATEGORY_NAMES.end()) {
            categories |= candidate->value;
        } else {
            Warn("unknown event group in DIVISION_CLR_EVENTS: {}", entry);
        }
    });

    return categories;
}

// ----- zones -----

// Colors are chosen against DivisionEngine/Profiling/ProfilerColors.cs: the engine's own zones are
// blue (phase), orange (behavior) and muted grey (idle), so the runtime gets the red end. A
// stop-the-world is the one thing on the timeline that no engine-side change can schedule around.
constexpr std::uint32_t COLOR_SUSPEND = 0xB03A3A;
constexpr std::uint32_t COLOR_STOPPED = 0x7C1F1F;
constexpr std::uint32_t COLOR_GC = 0x8F4FBF;
constexpr std::uint32_t COLOR_JIT = 0x3F8F6F;
constexpr std::uint32_t COLOR_LOADER = 0x8F7F3F;
constexpr std::uint32_t COLOR_DEEP = 0x4F6F8F;

/// A zone call site. The file and line are the caller's, so the profiler's UI still lands on the
/// code that opened the zone - there is no managed call site to point at.
consteval ___tracy_source_location_data Zone(const char* name, std::uint32_t color, std::source_location site = std::source_location::current()) {
    return {name, "DivisionClrProfiler", site.file_name(), site.line(), color};
}

/// One source location per suspend reason, indexed by COR_PRF_SUSPEND_REASON. Distinct locations
/// rather than one zone renamed per event, because Tracy aggregates its statistics per call site:
/// sharing one would collapse "how long does the runtime stop for a GC" into the same row as
/// "how long for a rejit". Same reasoning as Profiler.ZoneNamed on the managed side.
constexpr std::array<___tracy_source_location_data, kSuspendReasonCount> SUSPEND_LOCATIONS = {{
    Zone("EE suspend: other", COLOR_SUSPEND),
    Zone("EE suspend: GC", COLOR_SUSPEND),
    Zone("EE suspend: appdomain shutdown", COLOR_SUSPEND),
    Zone("EE suspend: code pitching", COLOR_SUSPEND),
    Zone("EE suspend: shutdown", COLOR_SUSPEND),
    Zone("EE suspend: unused", COLOR_SUSPEND),
    Zone("EE suspend: in-proc debugger", COLOR_SUSPEND),
    Zone("EE suspend: GC prep", COLOR_SUSPEND),
    Zone("EE suspend: rejit", COLOR_SUSPEND),
    Zone("EE suspend: profiler", COLOR_SUSPEND),
}};

constexpr ___tracy_source_location_data STOPPED_LOCATION = Zone("EE stopped", COLOR_STOPPED);
constexpr std::array<___tracy_source_location_data, 3> GC_LOCATIONS = {{
    Zone("GC gen0", COLOR_GC),
    Zone("GC gen1", COLOR_GC),
    Zone("GC gen2", COLOR_GC),
}};
constexpr ___tracy_source_location_data GC_UNKNOWN_LOCATION = Zone("GC", COLOR_GC);
constexpr ___tracy_source_location_data JIT_LOCATION = Zone("JIT", COLOR_JIT);
constexpr ___tracy_source_location_data MODULE_LOCATION = Zone("Module load", COLOR_LOADER);

/// The zones this profiler holds open across two callbacks, as one stack per thread.
///
/// Tracy models a thread's zones as a stack: they must close in the order they opened. Runtime
/// events do not always oblige. A background collection starts inside a suspension and outlives it,
/// so on one thread "GC gen2" and "EE stopped" genuinely cross. Where that happens the crossing
/// zone is closed and reopened immediately, which splits it into abutting segments instead of
/// either losing it or corrupting the capture - and a capture with a single bad pair is rejected
/// whole ("Invalid order of zone begin and end events"), so this is not a case to leave to luck.
enum ZoneKey : std::uint8_t {
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
    ZoneKey key;
    bool emitted;
};

/// How deep the stack may get. Ordinarily a handful of zones nest; deep mode nests one per managed
/// frame, so the limit doubles as the cap on how much of a call tree is recorded.
constexpr int STACK_LIMIT = 32;
constexpr int DEEP_STACK_LIMIT_DEFAULT = 64;

int g_stack_limit = STACK_LIMIT;

/// How many nested managed frames deep mode records before it stops opening zones. A cap rather
/// than a fixed size: a recursive descent parser will happily nest a thousand frames, and the
/// interesting part is the top of that, not the bottom.
int DeepStackLimit() {
    const std::optional<std::string> value = ReadEnvironment("DIVISION_CLR_DEEP_DEPTH");
    if (value.has_value()) {
        const int parsed = std::atoi(value->c_str());
        if (parsed > 0) {
            return parsed;
        }
    }

    return DEEP_STACK_LIMIT_DEFAULT;
}

/// Per thread, grown on demand. Not a fixed array: deep mode needs hundreds of entries and every
/// other mode needs a handful, and paying the deep size in thread-local storage on every thread of
/// a process that is not deep profiling would be the wrong trade.
struct ZoneStack {
    /// Every slot up to the capacity; only the first depth of them are open zones.
    std::vector<OpenZone> items;
    int depth = 0;

    /// Zones that could not be opened because the stack was full, so that their close still balances.
    std::array<unsigned, kKeyCount> dropped = {};

    unsigned& Dropped(ZoneKey key) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-constant-array-index): every ZoneKey but kKeyCount is in range, and kKeyCount is never passed.
        return dropped[key];
    }

    bool Reserve(int wanted) {
        const int capacity = static_cast<int>(items.size());
        if (wanted <= capacity) {
            return true;
        }

        const int grown = capacity == 0 ? 16 : capacity * 2;
        try {
            items.resize(static_cast<std::size_t>(std::max(grown, wanted)));
        } catch (const std::bad_alloc&) {
            // Out of memory is a dropped zone, not an exception escaping into the runtime.
            return false;
        }

        return true;
    }
};

thread_local ZoneStack t_zones;

std::atomic<std::uint32_t> g_splits{0};

constexpr std::string_view CONTINUED = "continued";

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
    const std::optional<std::string> value = ReadEnvironment("DIVISION_CLR_START_TRACY");
    if (!value.has_value() || !value->starts_with('1') || ___tracy_profiler_started() != 0) {
        return;
    }

    ___tracy_startup_profiler();
    Report("started the Tracy client");
}

void Annotate(OpenZone* zone, std::string_view text) {
    if (zone != nullptr && zone->emitted && TracyReady()) {
        ___tracy_emit_zone_text(zone->context, text.data(), text.size());
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

void PushZone(ZoneKey key, const ___tracy_source_location_data& location) {
    ZoneStack& stack = t_zones;
    if (stack.depth >= g_stack_limit || !stack.Reserve(stack.depth + 1)) {
        ++stack.Dropped(key);
        return;
    }

    OpenZone& zone = stack.items[stack.depth++];
    zone.location = &location;
    zone.key = key;
    OpenAt(zone);
}

OpenZone* TopZone(ZoneKey key) {
    ZoneStack& stack = t_zones;
    for (int index = stack.depth - 1; index >= 0; --index) {
        if (stack.items[index].key == key) {
            return &stack.items[index];
        }
    }

    return nullptr;
}

void PopZone(ZoneKey key) {
    ZoneStack& stack = t_zones;
    if (unsigned& dropped = stack.Dropped(key); dropped > 0) {
        --dropped;
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
        Annotate(&stack.items[index - 1], CONTINUED);
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
/// a method is the slot at a known index. CorProfSlots.h supplies the indices, one enum per interface.
template <typename Fn, typename Slot>
Fn Method(void* object, Slot slot) {
    auto** vtable = *static_cast<void***>(object);
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic): a vtable is a bare array of unknown length; the slot indices come from the ABI.
    return reinterpret_cast<Fn>(vtable[static_cast<std::size_t>(slot)]);
}

void* g_info = nullptr;

// UTF-16: a pair of surrogates carries 10 bits each of a code point above the basic plane.
constexpr char32_t HIGH_SURROGATE_FIRST = 0xD800;
constexpr char32_t LOW_SURROGATE_FIRST = 0xDC00;
constexpr char32_t SURROGATE_LAST = 0xDFFF;
constexpr char32_t SUPPLEMENTARY_FIRST = 0x10000;
constexpr int SURROGATE_BITS = 10;
constexpr char32_t REPLACEMENT_CHARACTER = 0xFFFD;

// UTF-8: a lead byte that says how many continuation bytes follow, each carrying 6 bits.
struct Utf8Form {
    char32_t limit;
    char32_t lead;
    int continuations;
};

constexpr std::array<Utf8Form, 4> UTF8_FORMS = {{
    {0x80, 0x00, 0},
    {0x800, 0xC0, 1},
    {0x10000, 0xE0, 2},
    {0x110000, 0xF0, 3},
}};
constexpr int CONTINUATION_BITS = 6;
constexpr char32_t CONTINUATION_MARK = 0x80;
constexpr char32_t CONTINUATION_MASK = 0x3F;

void AppendUtf8(std::string& result, char32_t code) {
    for (const Utf8Form& form : UTF8_FORMS) {
        if (code < form.limit) {
            result.push_back(static_cast<char>(form.lead | (code >> (CONTINUATION_BITS * form.continuations))));
            for (int index = form.continuations - 1; index >= 0; --index) {
                result.push_back(static_cast<char>(CONTINUATION_MARK | ((code >> (CONTINUATION_BITS * index)) & CONTINUATION_MASK)));
            }

            return;
        }
    }
}

/// UTF-16 to UTF-8, for the wide strings the runtime returns. Lone surrogates are passed through as
/// U+FFFD rather than rejected: this is display text for a timeline, not a round trip.
std::string ToUtf8(std::span<const Wchar> text) {
    std::string result;
    result.reserve(text.size());

    for (std::size_t index = 0; index < text.size(); ++index) {
        char32_t code = text[index];
        const char32_t next = index + 1 < text.size() ? text[index + 1] : 0;
        const bool high = code >= HIGH_SURROGATE_FIRST && code < LOW_SURROGATE_FIRST;
        if (high && next >= LOW_SURROGATE_FIRST && next <= SURROGATE_LAST) {
            code = SUPPLEMENTARY_FIRST + ((code - HIGH_SURROGATE_FIRST) << SURROGATE_BITS) + (next - LOW_SURROGATE_FIRST);
            ++index;
        } else if (code >= HIGH_SURROGATE_FIRST && code <= SURROGATE_LAST) {
            code = REPLACEMENT_CHARACTER;
        }

        AppendUtf8(result, code);
    }

    return result;
}

/// How many characters a name buffer passed to the runtime holds.
constexpr std::size_t NAME_CAPACITY = 512;

using NameBuffer = std::array<Wchar, NAME_CAPACITY>;

/// The name the runtime wrote into a buffer. Its count includes the terminator.
std::string ToUtf8(const NameBuffer& buffer, Ulong written) {
    return ToUtf8(std::span(buffer).first(std::min<std::size_t>(written - 1, buffer.size())));
}

std::string ModuleName(ModuleId module_id) {
    if (g_info == nullptr) {
        return {};
    }

    auto get_module_info = Method<GetModuleInfoFn>(g_info, InfoSlot::GetModuleInfo);
    NameBuffer buffer{};
    Ulong written = 0;
    if (get_module_info(g_info, module_id, nullptr, static_cast<Ulong>(buffer.size()), &written, buffer.data(), nullptr) != kOk || written == 0) {
        return {};
    }

    return ToUtf8(buffer, written);
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

constexpr ___tracy_source_location_data DEEP_UNKNOWN_LOCATION = Zone("managed", COLOR_DEEP);

/// Stands for "this method is deliberately not instrumented". Its address is a sentinel in the
/// name table; the zone itself is never emitted.
constexpr ___tracy_source_location_data DEEP_EXCLUDED_LOCATION = Zone("excluded", 0);
constexpr ___tracy_source_location_data DYNAMIC_METHOD_LOCATION = Zone("dynamic method", COLOR_DEEP);

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
constexpr std::string_view DEFAULT_DEEP_EXCLUDE = "DivisionEngine.Profiler.,DivisionEngine.ProfilerZone.";

std::vector<std::string> g_deep_exclude;

void LoadDeepExclusions() {
    const std::optional<std::string> value = ReadEnvironment("DIVISION_CLR_DEEP_EXCLUDE");
    ForEachListEntry(value.has_value() ? std::string_view(*value) : DEFAULT_DEEP_EXCLUDE, [](std::string_view entry) {
        if (!entry.empty()) {
            g_deep_exclude.emplace_back(entry);
        }
    });
}

bool IsExcluded(const std::string& name) {
    return std::ranges::any_of(g_deep_exclude, [&](const std::string& prefix) { return name.starts_with(prefix); });
}

/// FunctionID to call site, written once per method and then read on every call.
///
/// Open addressing with atomic slots rather than a map behind a lock: the write side is the JIT,
/// which is rare, and the read side is every managed call on every thread, which must not contend.
/// A method whose name never arrives, and anything past a full table, falls back to "managed".
class FunctionNames {
  public:
    static constexpr std::size_t CAPACITY = 1u << 16;

    const ___tracy_source_location_data* Find(FunctionId id) const {
        for (std::size_t probe = 0; probe < MAX_PROBE; ++probe) {
            const Slot& slot = SlotAt(id, probe);
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
        std::lock_guard<std::mutex> guard(mutex);
        for (std::size_t probe = 0; probe < MAX_PROBE; ++probe) {
            Slot& slot = SlotAt(id, probe);
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

    static constexpr std::size_t MAX_PROBE = 32;

    Slot& SlotAt(FunctionId id, std::size_t probe) const {
        // FunctionIDs are pointers; the low bits are alignment, so mix before masking.
        const auto hash = static_cast<std::size_t>((id >> 4) * 0x9E3779B97F4A7C15ull);
        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-constant-array-index): masked by CAPACITY - 1, and CAPACITY is a power of two.
        return slots[(hash + probe) & (CAPACITY - 1)];
    }

    mutable std::array<Slot, CAPACITY> slots;
    std::mutex mutex;
};

FunctionNames& Names() {
    static FunctionNames names;
    return names;
}

/// Copies a string where the profiler can read it whenever it likes. Never freed: one per method,
/// bounded by the methods the process compiles.
const char* DupUtf8(const std::string& value) {
    // NOLINTNEXTLINE(cppcoreguidelines-no-malloc,cppcoreguidelines-owning-memory): deliberately never freed, as above.
    auto* buffer = static_cast<char*>(std::malloc(value.size() + 1));
    if (buffer != nullptr) {
        std::memcpy(buffer, value.c_str(), value.size() + 1);
    }

    return buffer;
}

/// "Type.Method", or empty if the metadata could not be read.
std::string MethodName(FunctionId function_id) {
    if (g_info == nullptr) {
        return {};
    }

    std::uintptr_t class_id = 0;
    ModuleId module_id = 0;
    MdToken token = 0;
    if (Method<GetFunctionInfoFn>(g_info, InfoSlot::GetFunctionInfo)(g_info, function_id, &class_id, &module_id, &token) != kOk) {
        return {};
    }

    void* import = nullptr;
    if (Method<GetModuleMetaDataFn>(g_info, InfoSlot::GetModuleMetaData)(g_info, module_id, kOpenForRead, &kIidMetaDataImport, &import) != kOk || import == nullptr) {
        return {};
    }

    std::string result;
    NameBuffer method{};
    Ulong method_length = 0;
    MdToken type_token = 0;
    auto get_method_props = Method<GetMethodPropsFn>(import, MetadataSlot::GetMethodProps);
    if (get_method_props(import, token, &type_token, method.data(), static_cast<Ulong>(method.size()), &method_length, nullptr, nullptr, nullptr, nullptr, nullptr) == kOk && method_length > 0) {
        NameBuffer type{};
        Ulong type_length = 0;
        auto get_type_def_props = Method<GetTypeDefPropsFn>(import, MetadataSlot::GetTypeDefProps);
        if (get_type_def_props(import, type_token, type.data(), static_cast<Ulong>(type.size()), &type_length, nullptr, nullptr) == kOk && type_length > 0) {
            result = ToUtf8(type, type_length);
            result += '.';
        }

        result += ToUtf8(method, method_length);
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
void RememberName(FunctionId function_id) {
    if (Names().Find(function_id) != nullptr) {
        return;
    }

    // Recorded either way, including when the name cannot be read. Presence in the table is what
    // "this method carries hooks" means, and the enter, leave and unwind paths all rely on that
    // being the same answer - a method that is pushed but not popped is as bad as the reverse.
    const std::string name = MethodName(function_id);
    if (name.empty()) {
        Names().Add(function_id, &DEEP_UNKNOWN_LOCATION);
        return;
    }

    if (IsExcluded(name)) {
        Names().Add(function_id, &DEEP_EXCLUDED_LOCATION);
        return;
    }

    const char* copied = DupUtf8(name);
    // NOLINTNEXTLINE(cppcoreguidelines-no-malloc): never freed, like the name - Tracy reads the location for as long as the capture runs.
    auto* location = copied == nullptr ? nullptr : static_cast<___tracy_source_location_data*>(std::malloc(sizeof(___tracy_source_location_data)));
    if (location == nullptr) {
        Names().Add(function_id, &DEEP_UNKNOWN_LOCATION);
        return;
    }

    location->name = copied;
    location->function = copied;
    location->file = __FILE__;
    location->line = 0;
    location->color = COLOR_DEEP;
    Names().Add(function_id, location);
}

/// Whether this function's frames carry a zone. Unknown means it was never compiled while we were
/// attached - code that was compiled ahead of time has no hooks - and excluded means it is one of
/// the methods that must not be wrapped.
bool IsInstrumented(FunctionId function_id, const ___tracy_source_location_data*& location) {
    location = Names().Find(function_id);
    return location != nullptr && location != &DEEP_EXCLUDED_LOCATION;
}

void DIVISION_STDCALL DeepEnter(FunctionId function_id, std::uintptr_t client_data, std::uintptr_t frame_info, void* argument_info) {
    (void)client_data;
    (void)frame_info;
    (void)argument_info;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(function_id, location)) {
        PushZone(kKeyDeep, *location);
    }
}

void DIVISION_STDCALL DeepLeave(FunctionId function_id, std::uintptr_t client_data, std::uintptr_t frame_info, void* return_range) {
    (void)client_data;
    (void)frame_info;
    (void)return_range;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(function_id, location)) {
        PopZone(kKeyDeep);
    }
}

/// A tail call replaces this frame with the callee's, so no leave will arrive for it. The callee
/// opens its own zone on entry and closes it on its own leave, which keeps the stack balanced.
void DIVISION_STDCALL DeepTailcall(FunctionId function_id, std::uintptr_t client_data, std::uintptr_t frame_info) {
    (void)client_data;
    (void)frame_info;

    const ___tracy_source_location_data* location = nullptr;
    if (IsInstrumented(function_id, location)) {
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

Hresult DIVISION_STDCALL Initialize(void* self, void* info_unknown) {
    (void)self;

    auto query_interface = Method<QueryInterfaceFn>(info_unknown, InfoSlot::QueryInterface);
    // ICorProfilerInfo5 is the first with SetEventMask2, and the high mask is what lets the GC
    // callbacks be requested without COR_PRF_MONITOR_GC and its loss of concurrent GC.
    if (query_interface(info_unknown, &kIidCorProfilerInfo5, &g_info) != kOk || g_info == nullptr) {
        Warn("the runtime does not offer ICorProfilerInfo5; not recording.");
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
        g_stack_limit = DeepStackLimit();
        LoadDeepExclusions();
    }

    auto set_event_mask2 = Method<SetEventMask2Fn>(g_info, InfoSlot::SetEventMask2);
    const Hresult status = set_event_mask2(g_info, low, high);
    if (status != kOk) {
        Warn("SetEventMask2({:#x}, {:#x}) failed with 0x{:08x}; not recording.", low, high, static_cast<std::uint32_t>(status));
        return status;
    }

    if (deep) {
        // The mapper has to be in place before the hooks are, or the first calls arrive carrying
        // raw FunctionIDs where the hooks expect a call site.
        const Hresult hooks = Method<SetEnterLeaveHooks2Fn>(g_info, InfoSlot::SetEnterLeaveFunctionHooks2)(
            g_info, reinterpret_cast<void*>(&DeepEnter), reinterpret_cast<void*>(&DeepLeave), reinterpret_cast<void*>(&DeepTailcall)
        );
        if (hooks != kOk) {
            Warn("deep mode unavailable (0x{:08x}).", static_cast<std::uint32_t>(hooks));
        } else {
            Report("deep mode on, stack limit {}, {} exclusion prefixes", g_stack_limit, g_deep_exclude.size());
        }
    }

    StartTracyIfAsked();
    Report("attached; events low={:#x} high={:#x}", low, high);
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
        Report("{} zones were split because the runtime's events crossed", splits);
    }

    return kOk;
}

Hresult DIVISION_STDCALL RuntimeSuspendStarted(void* self, Dword reason) {
    (void)self;
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-constant-array-index): reasons past the table fall back to kSuspendOther.
    PushZone(kKeySuspend, SUSPEND_LOCATIONS[reason < kSuspendReasonCount ? reason : kSuspendOther]);
    return kOk;
}

/// All threads are now parked. The inner zone separates "getting everyone to stop" from "everyone
/// is stopped", which is the difference between a thread that will not reach a safe point and a
/// genuinely long pause.
Hresult DIVISION_STDCALL RuntimeSuspendFinished(void* self) {
    (void)self;
    PushZone(kKeyStopped, STOPPED_LOCATION);
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

Hresult DIVISION_STDCALL GarbageCollectionStarted(void* self, int generation_count, Bool32* generation_collected, Dword reason) {
    (void)self;

    // The array is indexed by COR_PRF_GC_GENERATION, where 3 and 4 are the large and pinned object
    // heaps. The generation a capture is read by is the highest ephemeral one.
    const std::span<const Bool32> collected(generation_collected, static_cast<std::size_t>(std::max(generation_count, 0)));
    const ___tracy_source_location_data* location = &GC_UNKNOWN_LOCATION;
    for (std::size_t index = 0; index < collected.size() && index < GC_LOCATIONS.size(); ++index) {
        if (collected[index] != 0) {
            // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-constant-array-index): the loop is bounded by its size.
            location = &GC_LOCATIONS[index];
        }
    }

    PushZone(kKeyGc, *location);
    if (reason == kGcInduced) {
        Annotate(TopZone(kKeyGc), "induced");
    }

    return kOk;
}

Hresult DIVISION_STDCALL GarbageCollectionFinished(void* self) {
    (void)self;
    PopZone(kKeyGc);
    return kOk;
}

Hresult DIVISION_STDCALL JitCompilationStarted(void* self, FunctionId function_id, Bool32 safe_to_block) {
    (void)self;
    (void)safe_to_block;

    if ((g_categories & kCategoryJit) == 0) {
        return kOk;
    }

    PushZone(kKeyJit, JIT_LOCATION);
    if (OpenZone* zone = TopZone(kKeyJit); zone != nullptr && zone->emitted && TracyReady()) {
        // The method's name would need IMetaDataImport, which is a second ABI to pin down. The id
        // is enough to tell one method's compilations apart and to count them.
        ___tracy_emit_zone_value(zone->context, static_cast<std::uint64_t>(function_id));
    }

    return kOk;
}

Hresult DIVISION_STDCALL JitCompilationFinished(void* self, FunctionId function_id, Hresult status, Bool32 safe_to_block) {
    (void)self;
    (void)safe_to_block;

    // The method now exists, so its metadata can be read - which is the documented place to do it,
    // and the reason deep mode does not resolve names from inside the hooks.
    if ((g_categories & kCategoryDeep) != 0 && status == kOk) {
        RememberName(function_id);
    }

    if ((g_categories & kCategoryJit) != 0) {
        PopZone(kKeyJit);
    }

    return kOk;
}

/// IL stubs, lambdas compiled through DynamicMethod, and the like. They are JIT compiled and so do
/// carry hooks, but they have no metadata to name them - they are recorded under one shared name so
/// that entering and leaving them stays consistent.
Hresult DIVISION_STDCALL DynamicMethodJitCompilationFinished(void* self, FunctionId function_id, Hresult status, Bool32 safe_to_block) {
    (void)self;
    (void)safe_to_block;

    if ((g_categories & kCategoryDeep) != 0 && status == kOk && Names().Find(function_id) == nullptr) {
        Names().Add(function_id, &DYNAMIC_METHOD_LOCATION);
    }

    return kOk;
}

Hresult DIVISION_STDCALL ModuleLoadStarted(void* self, ModuleId module_id) {
    (void)self;
    (void)module_id;

    PushZone(kKeyModule, MODULE_LOCATION);
    return kOk;
}

Hresult DIVISION_STDCALL ModuleLoadFinished(void* self, ModuleId module_id, Hresult status) {
    (void)self;
    (void)status;

    // Named here rather than at the start: the module has no name until it has been loaded.
    if (OpenZone* zone = TopZone(kKeyModule); zone != nullptr && zone->emitted) {
        const std::string name = ModuleName(module_id);
        if (!name.empty()) {
            Annotate(zone, name);
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
Hresult DIVISION_STDCALL ExceptionUnwindFunctionEnter(void* self, FunctionId function_id) {
    (void)self;

    const ___tracy_source_location_data* location = nullptr;
    if ((g_categories & kCategoryDeep) != 0 && IsInstrumented(function_id, location)) {
        PopZone(kKeyDeep);
    }

    return kOk;
}

// ----- the COM object -----

struct CallbackVtable {
    std::array<void*, static_cast<std::size_t>(CallbackSlot::SlotCount)> slots;
};

struct CallbackObject {
    const CallbackVtable* vtable;
};

Hresult DIVISION_STDCALL CallbackQueryInterface(void* self, const Guid* iid, void** result);
Ulong DIVISION_STDCALL CallbackAddRef(void*);
Ulong DIVISION_STDCALL CallbackRelease(void*);

template <typename Fn>
void Put(CallbackVtable& table, CallbackSlot slot, Fn function) {
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-constant-array-index): every CallbackSlot but SlotCount is in range, and SlotCount is never passed.
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

/// IUnknown's three methods, then IClassFactory's CreateInstance and LockServer.
constexpr std::size_t FACTORY_SLOT_COUNT = 5;

struct FactoryVtable {
    std::array<void*, FACTORY_SLOT_COUNT> slots;
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
        const std::optional<std::string> verbose = ReadEnvironment("DIVISION_CLR_VERBOSE");
        g_verbose = verbose.has_value() && verbose->starts_with('1');
        g_categories = ParseCategories(ReadEnvironment("DIVISION_CLR_EVENTS"));
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
