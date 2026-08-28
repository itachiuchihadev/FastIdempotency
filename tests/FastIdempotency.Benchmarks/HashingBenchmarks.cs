using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastIdempotency.Core.Hashing;

namespace FastIdempotency.Benchmarks;

/// <summary>
/// Microbenchmarks comparing XxHash3 vs SHA256 vs MD5 for request fingerprinting.
/// Demonstrates zero-allocation performance of FastIdempotency's SIMD-accelerated XxHash3 hasher.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class HashingBenchmarks
{
    private readonly XxHash3RequestHasher _xxHasher = new();
    private byte[] _payload = null!;

    [Params(128, 1024, 10 * 1024, 100 * 1024)]
    public int PayloadSizeBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSizeBytes];
        Random.Shared.NextBytes(_payload);
    }

    [Benchmark(Baseline = true, Description = "XxHash3 (FastIdempotency)")]
    public ulong XxHash3_Compute()
    {
        return _xxHasher.ComputeHash("POST", "/api/v1/checkout/orders", _payload);
    }

    [Benchmark(Description = "SHA256")]
    public byte[] Sha256_Compute()
    {
        return SHA256.HashData(_payload);
    }

    [Benchmark(Description = "MD5")]
    public byte[] Md5_Compute()
    {
        return MD5.HashData(_payload);
    }
}
