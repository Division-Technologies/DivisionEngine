namespace DivisionEngine;

/// <summary>
///     Marks a type as an entity component. The attribute carries no data at runtime; it tells the
///     source generator to emit a <c>[ModuleInitializer]</c> that registers the type with
///     <see cref="ComponentTypeRegistry" /> as soon as its assembly loads.
///     Without that, a component type only enters the registry when some code mentions
///     <see cref="ComponentType{T}" />, so loading a scene saved by an earlier run could not map the
///     persisted type id back to a <see cref="ComponentTypeId" /> without constructing the generic
///     type reflectively — which the engine avoids for AOT compatibility.
///     Pair it with <c>[AutoSerialization]</c> to make the component's fields persistable, and with
///     <see cref="TypeIdAttribute" /> before renaming the type.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class ComponentAttribute : Attribute;
