using BenchmarkDotNet.Running;

namespace FastIdempotency.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        // Use BenchmarkSwitcher so benchmarks can be selected via command-line arguments or an interactive menu:
        // Examples:
        //   dotnet run -c Release -- --filter *Hashing*
        //   dotnet run -c Release -- --filter *Middleware*
        //   dotnet run -c Release -- --filter *BodyCapture*
        //   dotnet run -c Release -- --all
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
