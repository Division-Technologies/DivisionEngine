using System.Reflection;
using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     Per-type static cache for <see cref="IValueFormatter{T}" />. Generated code reads
///     <see cref="Formatter" /> for every field that is not a primitive/string/enum.
/// </summary>
public static class FormatterStore<T>
{
    internal static IValueFormatter<T>? Stored;

    public static IValueFormatter<T> Formatter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Stored ?? FormatterRegistry.Resolve<T>();
    }
}

public static class FormatterRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Type> OpenGenericFactories = new();
    private static readonly HashSet<Assembly> ScannedAssemblies = new();

    public static void Register<T>(IValueFormatter<T> formatter)
    {
        FormatterStore<T>.Stored = formatter;
    }

    /// <summary>
    ///     Registers an open-generic formatter (e.g. typeof(List&lt;&gt;) → typeof(ListFormatter&lt;&gt;)).
    ///     Type arguments are mapped 1:1 when the formatter is closed on demand.
    /// </summary>
    public static void RegisterFactory(Type openGenericTarget, Type openGenericFormatter)
    {
        if (!openGenericTarget.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"{openGenericTarget} is not an open generic type.", nameof(openGenericTarget));
        }

        if (!openGenericFormatter.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"{openGenericFormatter} is not an open generic type.",
                nameof(openGenericFormatter));
        }

        lock (Gate)
        {
            OpenGenericFactories[openGenericTarget] = openGenericFormatter;
        }
    }

    internal static IValueFormatter<T> Resolve<T>()
    {
        lock (Gate)
        {
            if (FormatterStore<T>.Stored is { } stored)
            {
                return stored;
            }

            // Module initializers of an assembly only run once something in that assembly is
            // touched. Force them for every loaded assembly that declares formatter registrations
            // so resolution does not depend on initialization order.
            RunPendingModuleInitializers();
            if (FormatterStore<T>.Stored is { } registered)
            {
                return registered;
            }

            var fallback = CreateFallback<T>();
            if (fallback is null)
            {
                throw new InvalidOperationException(
                    $"No IValueFormatter<{typeof(T)}> is registered. " +
                    $"Annotate a formatter with [CustomFormatter(typeof({typeof(T).Name}))] or call FormatterRegistry.Register.");
            }

            FormatterStore<T>.Stored = fallback;
            return fallback;
        }
    }

    private static IValueFormatter<T>? CreateFallback<T>()
    {
        var type = typeof(T);

        if (type.IsEnum)
        {
            return (IValueFormatter<T>)Activator.CreateInstance(typeof(EnumFormatter<>).MakeGenericType(type))!;
        }

        if (typeof(ISerializableObject).IsAssignableFrom(type) && type.IsClass)
        {
            return (IValueFormatter<T>)Activator.CreateInstance(
                typeof(ObjectReferenceFormatter<>).MakeGenericType(type))!;
        }

        if (type.IsArray && type.GetArrayRank() == 1 && type.GetElementType() is { } element)
        {
            return (IValueFormatter<T>)Activator.CreateInstance(typeof(ArrayFormatter<>).MakeGenericType(element))!;
        }

        if (type.IsConstructedGenericType &&
            OpenGenericFactories.TryGetValue(type.GetGenericTypeDefinition(), out var openFormatter))
        {
            return (IValueFormatter<T>)Activator.CreateInstance(
                openFormatter.MakeGenericType(type.GenericTypeArguments))!;
        }

        return null;
    }

    private static void RunPendingModuleInitializers()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            if (!ScannedAssemblies.Add(assembly))
            {
                continue;
            }

            if (!assembly.IsDefined(typeof(FormatterRegistrationAttribute)))
            {
                continue;
            }

            try
            {
                RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            }
            catch
            {
                // best effort — a failing module initializer surfaces on first real use
            }
        }
    }
}