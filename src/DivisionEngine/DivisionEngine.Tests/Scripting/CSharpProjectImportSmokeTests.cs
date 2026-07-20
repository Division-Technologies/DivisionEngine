using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Scripting;

/// <summary>
///     Exercises the real MSBuild-based compile path. Marked [Explicit] because it depends on a
///     usable .NET SDK / MSBuild BuildHost in the environment; run it on demand to validate that
///     the BuildHost approach works (no MSBuildLocator).
/// </summary>
[TestFixture]
[Explicit("Requires a .NET SDK / MSBuild BuildHost.")]
public sealed class CSharpProjectImportSmokeTests
{
    [Test]
    public void ImportCsproj_CompilesUserComponent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DivisionScriptSmoke", Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(dir, "Assets");
        Directory.CreateDirectory(assets);

        var engineDll = typeof(ISerializableObject).Assembly.Location;
        File.WriteAllText(Path.Combine(assets, "Scripts.csproj"), $"""
                                                                   <Project Sdk="Microsoft.NET.Sdk">
                                                                     <PropertyGroup>
                                                                       <TargetFramework>net10.0</TargetFramework>
                                                                       <Nullable>enable</Nullable>
                                                                       <LangVersion>preview</LangVersion>
                                                                       <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                                                                     </PropertyGroup>
                                                                     <ItemGroup>
                                                                       <Reference Include="DivisionEngine"><HintPath>{engineDll}</HintPath></Reference>
                                                                     </ItemGroup>
                                                                   </Project>
                                                                   """);
        File.WriteAllText(Path.Combine(assets, "Greeter.cs"), """
                                                              using DivisionEngine;
                                                              namespace UserScripts;
                                                              public sealed class Greeter : Component, ISerializable
                                                              {
                                                                  public int Value;
                                                                  public void Serialize<T>(ref T s) where T : ISerializer, allows ref struct => s.I32(0, default, Value);
                                                                  public void Deserialize<T>(ref T d) where T : IDeserializer, allows ref struct => Value = d.I32(0, default);
                                                              }
                                                              """);

        try
        {
            using var db = new AssetDatabase([assets], Path.Combine(dir, "Library"));
            db.Refresh();

            var compiled = db.LoadAsset<CompiledAssembly>(Path.Combine(assets, "Scripts.csproj"));
            Assert.That(compiled, Is.Not.Null);
            Assert.That(compiled!.Success, Is.True,
                "compile failed:\n" + string.Join("\n", compiled.Diagnostics));
            Assert.That(File.Exists(compiled.DllPath), Is.True);

            // bin/obj must not be created next to the .csproj in the Assets tree.
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(assets, "obj")), Is.False, "obj leaked into Assets");
                Assert.That(Directory.Exists(Path.Combine(assets, "bin")), Is.False, "bin leaked into Assets");
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}