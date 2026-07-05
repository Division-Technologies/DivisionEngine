namespace DivisionEngine.Tests.Serialization;

[TestFixture]
public sealed class FormatterRegistryTests
{
    private sealed class UnregisteredPlainClass
    {
    }

    [Test]
    public void BuiltIn_Primitive_Resolves()
    {
        Assert.That(FormatterStore<int>.Formatter, Is.Not.Null);
    }

    [Test]
    public void ByteArray_UsesDedicatedBlobFormatter()
    {
        Assert.That(FormatterStore<byte[]>.Formatter, Is.TypeOf<ByteArrayFormatter>());
    }

    [Test]
    public void OpenGenericFactory_ClosesListFormatter()
    {
        Assert.That(FormatterStore<List<double>>.Formatter, Is.TypeOf<ListFormatter<double>>());
    }

    [Test]
    public void Enum_FallsBackToEnumFormatter()
    {
        Assert.That(FormatterStore<TestEnum>.Formatter, Is.TypeOf<EnumFormatter<TestEnum>>());
    }

    [Test]
    public void Array_FallsBackToArrayFormatter()
    {
        Assert.That(FormatterStore<float[]>.Formatter, Is.TypeOf<ArrayFormatter<float>>());
    }

    [Test]
    public void CustomFormatter_RegisteredByModuleInitializer()
    {
        Assert.That(FormatterStore<Version>.Formatter, Is.TypeOf<VersionFormatter>());
    }

    [Test]
    public void AutoSerializationStruct_RegisteredByModuleInitializer()
    {
        Assert.That(FormatterStore<TestPoint>.Formatter, Is.SameAs(TestPoint.Formatter));
    }

    [Test]
    public void Unregistered_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _ = FormatterStore<UnregisteredPlainClass>.Formatter);
    }
}