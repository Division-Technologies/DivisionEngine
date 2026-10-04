using BenchmarkDotNet.Running;

// Run all: dotnet run -c Release --project DivisionEngine.Benchmarks
// Filter:  dotnet run -c Release --project DivisionEngine.Benchmarks -- --filter *TimeProvider*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);