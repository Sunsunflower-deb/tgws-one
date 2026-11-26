using System.Runtime.InteropServices;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// A page-aligned unmanaged buffer for <c>vmsplice(SPLICE_F_GIFT)</c>. <see cref="NativeMemory.AlignedAlloc"/> would
/// be the natural choice, but it returns <c>void*</c> and this project does not enable <c>AllowUnsafeBlocks</c>, so the
/// same libc primitive it wraps (<c>posix_memalign</c>) is P/Invoked with an <see cref="IntPtr"/> result. The matching
/// <see cref="NativeMemory.AlignedFree"/> is <c>free</c>. The allocation is rounded up to whole pages so the kernel
/// owns complete pages when the buffer is gifted.
/// </summary>
internal sealed class PageAlignedBuffer : IDisposable
{
    private IntPtr _pointer;

    public PageAlignedBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        if (length == 0) return;

        nuint pageSize = (nuint)Environment.SystemPageSize;
        nuint allocationSize = ((nuint)length + pageSize - 1) / pageSize * pageSize;
        int rc = PosixMemAlign(out _pointer, pageSize, allocationSize);
        if (rc != 0)
        {
            _pointer = IntPtr.Zero;
            throw new OutOfMemoryException($"posix_memalign failed with errno {rc}");
        }
    }

    /// <summary>The number of usable bytes the caller asked for, not the rounded-up allocation size.</summary>
    public int Length { get; }

    internal IntPtr Pointer => _pointer;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > Length) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes.IsEmpty) return;
        Marshal.Copy(bytes.ToArray(), 0, _pointer, bytes.Length);
    }

    public void Dispose()
    {
        if (_pointer == IntPtr.Zero) return;
        Free(_pointer);
        _pointer = IntPtr.Zero;
    }

    [DllImport("libc", EntryPoint = "posix_memalign", SetLastError = true)]
    private static extern int PosixMemAlign(out IntPtr pointer, nuint alignment, nuint size);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
