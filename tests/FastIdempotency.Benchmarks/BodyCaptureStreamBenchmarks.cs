using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastIdempotency.AspNetCore.Internals;

namespace FastIdempotency.Benchmarks;

/// <summary>
/// Microbenchmarks comparing FastIdempotency's RecyclableMemoryStream-backed BodyCaptureStream
/// against naive MemoryStream buffering to demonstrate zero Large Object Heap (LOH) fragmentation
/// and reduced GC allocations on HTTP response captures.
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class BodyCaptureStreamBenchmarks
{
    private byte[] _responseChunk = null!;
    private Stream _destination = null!;

    [Params(1024, 64 * 1024, 256 * 1024)]
    public int ResponseSizeBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _responseChunk = new byte[ResponseSizeBytes];
        Random.Shared.NextBytes(_responseChunk);
        _destination = Stream.Null;
    }

    [Benchmark(Baseline = true, Description = "BodyCaptureStream (Pooled RecyclableStream)")]
    public byte[] BodyCaptureStream_WriteAndCapture()
    {
        using var captureStream = new BodyCaptureStream(_destination);
        captureStream.Write(_responseChunk, 0, _responseChunk.Length);
        captureStream.Flush();
        return captureStream.GetCapturedBytes();
    }

    [Benchmark(Description = "Naive MemoryStream Tee-Capture")]
    public byte[] NaiveMemoryStream_WriteAndCapture()
    {
        using var memoryStream = new MemoryStream();
        _destination.Write(_responseChunk, 0, _responseChunk.Length);
        memoryStream.Write(_responseChunk, 0, _responseChunk.Length);
        _destination.Flush();
        return memoryStream.ToArray();
    }
}
