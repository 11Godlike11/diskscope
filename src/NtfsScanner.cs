using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Collections.Concurrent;

namespace DiskScope;

static class NtfsScanner
{
    static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    static ulong U64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b[p..]);
    static long I64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b[p..]);
    internal static bool Fixup(Span<byte> b, bool cooked = false)
    {
        if (b.Length < 48 || U32(b, 0) != 0x454c4946) return false;
        int off = U16(b, 4), count = U16(b, 6);
        if (off < 8 || off + count * 2 > b.Length || count < 2 || (count - 1) * 512 != b.Length) return false;
        ushort seq = U16(b, off);
        for (int i = 1; i < count; i++)
        {
            int end = 512 * i - 2; // NTFS update-sequence stride is independent of the volume's sector size.
            if (U16(b, end) != seq) { if (cooked) continue; return false; }
            b[end] = b[off + 2 * i]; b[end + 1] = b[off + 2 * i + 1];
        }
        return true;
    }
    ref struct AttributeEnumerator(ReadOnlySpan<byte> record)
    {
        readonly ReadOnlySpan<byte> b = record;
        int next = U16(record, 20);
        readonly int used = Math.Min(checked((int)U32(record, 24)), record.Length);
        public (int Offset, int Length) Current { get; private set; }
        public AttributeEnumerator GetEnumerator() => this;
        public bool MoveNext()
        {
            if (next < 48 || next + 16 > used || U32(b, next) == uint.MaxValue) return false;
            int len = checked((int)U32(b, next + 4));
            if (len < 24 || len > used - next) throw new InvalidDataException("Invalid MFT attribute bounds");
            Current = (next, len); next += len; return true;
        }
    }
    static AttributeEnumerator Attributes(ReadOnlySpan<byte> b) => new(b);
    ref struct RunEnumerator(ReadOnlySpan<byte> attribute)
    {
        readonly ReadOnlySpan<byte> a = attribute;
        long vcn = I64(attribute, 16), lcn;
        int p = U16(attribute, 32);
        public Run Current { get; private set; }
        public RunEnumerator GetEnumerator() => this;
        public bool MoveNext()
        {
            if (p >= a.Length || a[p] == 0) return false;
            int nl = a[p] & 15, no = a[p++] >> 4;
            if (nl < 1 || nl > 8 || no > 8 || p + nl + no > a.Length) throw new InvalidDataException("Invalid MFT runlist");
            ulong len = 0; for (int i = 0; i < nl; i++) len |= (ulong)a[p++] << (i * 8);
            long delta = 0; for (int i = 0; i < no; i++) delta |= (long)a[p++] << (i * 8);
            if (no > 0 && no < 8 && (delta & (1L << (no * 8 - 1))) != 0) delta |= -1L << (no * 8);
            if (no > 0) lcn = checked(lcn + delta);
            if (len == 0 || len > long.MaxValue) throw new InvalidDataException("Invalid extent length");
            Current = new(vcn, no == 0 ? -1 : lcn, (long)len); vcn = checked(vcn + (long)len); return true;
        }
    }
    static RunEnumerator Runs(ReadOnlySpan<byte> a) => new(a);
    internal readonly record struct ReadSegment(long Offset, long Length);
    internal static List<ReadSegment> BitmapSegments(ReadOnlySpan<byte> bitmap, long slots, int recordSize, int gapBytes = 8 * 1024)
    {
        if (slots < 1 || (slots + 7) / 8 > bitmap.Length) throw new InvalidDataException("Incomplete MFT bitmap");
        const int blockBytes = 4 * 1024; int blockSlots = Math.Max(8, blockBytes / recordSize); var segments = new List<ReadSegment>();
        long start = -1, end = 0;
        for (long first = 0; first < slots; first += blockSlots)
        {
            long last = Math.Min(slots, first + blockSlots); bool used = false;
            for (long p = first / 8; p < (last + 7) / 8; p++) if (bitmap[(int)p] != 0) { used = true; break; }
            if (!used) continue;
            long offset = first * recordSize, limit = last * recordSize;
            if (start >= 0 && offset - end <= gapBytes) end = limit;
            else { if (start >= 0) segments.Add(new(start, end - start)); start = offset; end = limit; }
        }
        if (start >= 0) segments.Add(new(start, end - start));
        if (segments.Count == 0 || (bitmap[0] & (1 << 5)) == 0) throw new InvalidDataException("MFT bitmap root missing");
        return segments;
    }
    internal static byte[] Record(SafeFileHandle volume, ulong id, int recordSize)
    {
        var output = Native.Ioctl(volume, 0x90068, BitConverter.GetBytes(id & 0xffffffffffffUL), recordSize + 16);
        if (output.Length < recordSize + 12 || (U64(output, 0) & 0xffffffffffffUL) != (id & 0xffffffffffffUL)) throw new InvalidDataException("MFT extension record unavailable");
        byte[] b = output[12..(12 + recordSize)];
        if (!Fixup(b, true)) throw new InvalidDataException("Invalid MFT bootstrap record");
        return b;
    }
    public static Snapshot Scan(string root, Action<long, long>? progress = null)
    {
        root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(root))!;
        if (root.Length != 3) throw new ArgumentException("NTFS scanning requires a local drive letter");
        var watch = Stopwatch.StartNew(); var s = new Snapshot { Root = root, CreatedUtc = DateTime.UtcNow };
        using var volume = Native.Open(@"\\.\" + root[..2], 0x80000000);
        byte[] v = Native.Ioctl(volume, 0x90064, null, 128);
        s.VolumeSerial = (uint)U64(v, 0);
        int sector = (int)U32(v, 40), cluster = (int)U32(v, 44), rs = (int)U32(v, 48); long valid = I64(v, 56);
        if (rs < 512 || rs > 65536 || cluster < sector || valid <= 0) throw new InvalidDataException("Unsupported NTFS geometry");
        // Some storage/filter stacks reject raw ReadFile while supporting NTFS control codes.
        try { Native.ReadAt(volume, I64(v, 64) * cluster, new byte[cluster], cluster); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1 or 5 or 50 or 87)
        {
            return ScanControlRecords(volume, s, valid, rs, cluster, watch, progress);
        }
        byte[] rec0 = Record(volume, 0, rs); var mftRuns = new List<Run>(); var attrLists = new List<byte[]>();
        var bitmapRuns = new List<Run>(); byte[]? residentBitmap = null; long bitmapSize = 0; bool bitmapInvalid = false;
        void Bootstrap(byte[] b)
        {
            foreach (var (off, len) in Attributes(b))
            {
                var a = b.AsSpan(off, len); uint type = U32(a, 0);
                if (type == 0x80 && a[9] == 0 && a[8] == 1) { foreach (var run in Runs(a)) mftRuns.Add(run); }
                if (type == 0xb0 && a[9] == 0)
                {
                    try
                    {
                        if (a[8] == 0) residentBitmap = a.Slice(U16(a, 20), checked((int)U32(a, 16))).ToArray();
                        else { if (I64(a, 16) == 0) bitmapSize = I64(a, 48); foreach (var run in Runs(a)) bitmapRuns.Add(run); }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { bitmapInvalid = true; }
                }
                if (type != 0x20) continue;
                if (a[8] == 0) attrLists.Add(a.Slice(U16(a, 20), (int)U32(a, 16)).ToArray());
                else
                {
                    long size = I64(a, 48); if (size > 16 * 1024 * 1024) throw new InvalidDataException("MFT attribute list too large");
                    byte[] list = new byte[(int)size]; int dest = 0;
                    foreach (var r in Runs(a))
                    {
                        int n = (int)Math.Min(size - dest, r.Clusters * cluster); if (n <= 0) break;
                        if (r.Lcn < 0) throw new InvalidDataException("Sparse MFT attribute list");
                        int aligned = ((n + sector - 1) / sector) * sector; byte[] chunk = new byte[aligned];
                        Native.ReadAt(volume, r.Lcn * cluster, chunk, aligned); Array.Copy(chunk, 0, list, dest, n); dest += n;
                    }
                    if (dest != size) throw new InvalidDataException("Incomplete MFT attribute list");
                    attrLists.Add(list);
                }
            }
        }
        Bootstrap(rec0); var extraIds = new HashSet<ulong>();
        for (int li = 0; li < attrLists.Count; li++)
        {
            var list = attrLists[li];
            int p = 0;
            while (p + 26 <= list.Length)
            {
                int len = U16(list, p + 4); if (len < 26 || p + len > list.Length) break;
                if ((U32(list, p) == 0x80 || U32(list, p) == 0xb0) && list[p + 6] == 0)
                {
                    ulong id = U64(list, p + 16) & 0xffffffffffffUL;
                    if (id != 0 && extraIds.Add(id))
                    {
                        try { Bootstrap(Record(volume, id, rs)); }
                        catch (Exception ex) when (U32(list, p) == 0xb0 && ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException or OverflowException) { bitmapInvalid = true; }
                    }
                }
                p += len;
            }
        }
        mftRuns = mftRuns.Distinct().OrderBy(r => r.Vcn).ToList();
        long expectedVcn = 0; foreach (var r in mftRuns) { if (r.Vcn != expectedVcn || r.Lcn < 0) throw new InvalidDataException("Incomplete MFT extent map"); expectedVcn += r.Clusters; }
        if (expectedVcn * cluster < valid) throw new InvalidDataException("MFT extents do not cover valid data");
        s.TotalRecordSlots = valid / rs;
        var segments = new List<ReadSegment> { new(0, valid) };
        if (!bitmapInvalid)
        {
            try
            {
                byte[] bitmap;
                if (residentBitmap != null) bitmap = residentBitmap;
                else
                {
                    if (bitmapSize < (s.TotalRecordSlots + 7) / 8 || bitmapSize > 256 * 1024 * 1024) throw new InvalidDataException("Invalid MFT bitmap size");
                    bitmap = new byte[checked((int)bitmapSize)];
                    bitmapRuns = bitmapRuns.Distinct().OrderBy(r => r.Vcn).ToList(); long dest = 0;
                    foreach (var run in bitmapRuns)
                    {
                        if (run.Vcn * cluster != dest || run.Lcn < 0) throw new InvalidDataException("Incomplete MFT bitmap map");
                        int n = (int)Math.Min(bitmapSize - dest, run.Clusters * cluster); if (n <= 0) break;
                        int aligned = checked((n + sector - 1) / sector * sector); byte[] chunk = new byte[aligned];
                        Native.ReadAt(volume, run.Lcn * cluster, chunk, aligned); chunk.AsSpan(0, n).CopyTo(bitmap.AsSpan((int)dest)); dest += n;
                    }
                    if (dest != bitmapSize) throw new InvalidDataException("Incomplete MFT bitmap");
                }
                segments = BitmapSegments(bitmap, s.TotalRecordSlots, rs); s.BitmapUsed = true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException or OverflowException) { /* Full reads remain the correctness fallback. */ }
        }
        s.ReadSeconds = watch.Elapsed.TotalSeconds;
        int workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
        var builders = new IndexBuilder[workers];
        using var queue = new BlockingCollection<(byte[] Buffer, int Length, long Offset)>(workers * 2);
        using var cancel = new CancellationTokenSource();
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            var builder = builders[w] = new IndexBuilder();
            tasks[w] = Task.Factory.StartNew(() =>
            {
                try
                {
                    foreach (var block in queue.GetConsumingEnumerable(cancel.Token))
                    {
                        long begin = Stopwatch.GetTimestamp();
                        try
                        {
                            for (int p = 0; p < block.Length; p += rs)
                            {
                                builder.Raw++; var b = block.Buffer.AsSpan(p, rs);
                                if (U32(b, 0) != 0x454c4946 || (U16(b, 22) & 1) == 0) continue;
                                if (!Fixup(b)) { builder.Invalid++; continue; }
                                ulong ordinal = (ulong)((block.Offset + p) / rs), id = ordinal | ((ulong)U16(b, 16) << 48);
                                try { var e = Parse(b, id, cluster); ulong baseId = U64(b, 32); if (baseId != 0) builder.Extensions.Add((baseId, e)); else builder.Add(e); }
                                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { builder.Invalid++; }
                            }
                        }
                        finally { ArrayPool<byte>.Shared.Return(block.Buffer); builder.ParseSeconds += Stopwatch.GetElapsedTime(begin).TotalSeconds; }
                    }
                }
                catch { cancel.Cancel(); throw; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        using var requests = new BlockingCollection<(long Physical, int Length, long Offset)>(workers * 2);
        var readTasks = new Task[2]; var readSeconds = new double[readTasks.Length];
        for (int w = 0; w < readTasks.Length; w++)
        {
            int reader = w;
            readTasks[w] = Task.Factory.StartNew(() =>
            {
                try
                {
                    // Each reader owns its file pointer; parsing never shares mutable buffers.
                    using var handle = Native.Open(@"\\.\" + root[..2], 0x80000000);
                    foreach (var request in requests.GetConsumingEnumerable(cancel.Token))
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(request.Length); bool queued = false;
                        try
                        {
                            long begin = Stopwatch.GetTimestamp(); Native.ReadAt(handle, request.Physical, buffer, request.Length); readSeconds[reader] += Stopwatch.GetElapsedTime(begin).TotalSeconds;
                            queue.Add((buffer, request.Length, request.Offset), cancel.Token); queued = true;
                        }
                        finally { if (!queued) ArrayPool<byte>.Shared.Return(buffer); }
                    }
                }
                catch { cancel.Cancel(); throw; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        try
        {
            int segmentIndex = 0;
            foreach (var r in mftRuns)
            {
                long runStart = r.Vcn * cluster, runEnd = Math.Min(valid, runStart + r.Clusters * cluster);
                while (segmentIndex < segments.Count && segments[segmentIndex].Offset + segments[segmentIndex].Length <= runStart) segmentIndex++;
                for (int j = segmentIndex; j < segments.Count && segments[j].Offset < runEnd; j++)
                {
                    long start = Math.Max(runStart, segments[j].Offset), end = Math.Min(runEnd, segments[j].Offset + segments[j].Length);
                    for (long pos = start; pos < end;)
                    {
                        int n = (int)Math.Min(4 * 1024 * 1024, end - pos); n -= n % rs; if (n == 0) break;
                        requests.Add((r.Lcn * cluster + pos - runStart, n, pos), cancel.Token);
                        pos += n; progress?.Invoke(pos / rs, valid / rs);
                    }
                }
            }
        }
        finally
        {
            requests.CompleteAdding();
            try { Task.WaitAll(readTasks); }
            finally
            {
                queue.CompleteAdding();
                try { Task.WaitAll(tasks); }
                finally { while (queue.TryTake(out var block)) ArrayPool<byte>.Shared.Return(block.Buffer); }
            }
        }
        s.ReadSeconds += readSeconds.Max();
        foreach (var b in builders) { s.RawRecords += b.Raw; s.InvalidRecords += b.Invalid; s.ParseSeconds = Math.Max(s.ParseSeconds, b.ParseSeconds); foreach (var n in b.Nodes) if (Snapshot.Ordinal(n.Id) == 5) s.RootId = n.Id; }
        s.Build(builders); s.Seconds = watch.Elapsed.TotalSeconds; return s;
    }
    static Snapshot ScanControlRecords(SafeFileHandle volume, Snapshot s, long valid, int rs, int cluster, Stopwatch watch, Action<long,long>? progress)
    {
        s.Backend = "ntfs-mft-ioctl";
        byte[] input = new byte[8], output = new byte[rs + 16]; ulong requested = (ulong)(valid / rs - 1);
        var builder = new IndexBuilder(); s.TotalRecordSlots = valid / rs;
        while (true)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input, requested);
            long readBegin = Stopwatch.GetTimestamp();
            if (!Native.DeviceIoControl(volume, 0x90068, input, 8, output, output.Length, out int n, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), $"MFT record query failed at {requested}");
            s.ReadSeconds += Stopwatch.GetElapsedTime(readBegin).TotalSeconds;
            if (n < rs + 12) throw new InvalidDataException("Short MFT record response");
            ulong ordinal = U64(output, 0) & 0xffffffffffffUL;
            if (ordinal > requested) throw new InvalidDataException("MFT enumeration did not advance");
            var b = output.AsSpan(12, rs); s.RawRecords++; long parseBegin = Stopwatch.GetTimestamp();
            if (!Fixup(b, true)) s.InvalidRecords++;
            else
            {
                try
                {
                    ulong id = ordinal | ((ulong)U16(b, 16) << 48); var e = Parse(b, id, cluster); ulong baseId = U64(b, 32);
                    if (baseId != 0) builder.Extensions.Add((baseId, e)); else builder.Add(e);
                    if (ordinal == 5) s.RootId = id;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { s.InvalidRecords++; }
            }
            s.ParseSeconds += Stopwatch.GetElapsedTime(parseBegin).TotalSeconds;
            if (s.RawRecords % 8192 == 0 || ordinal == 0) progress?.Invoke(valid / rs - (long)ordinal, valid / rs);
            if (ordinal == 0) break; requested = ordinal - 1;
        }
        s.Build([builder]); s.Seconds = watch.Elapsed.TotalSeconds; return s;
    }
    internal static Entry Parse(ReadOnlySpan<byte> b, ulong id, int cluster)
    {
        var e = new Entry { Id = id, Directory = (U16(b, 22) & 2) != 0, Links = U16(b, 18) };
        foreach (var (off, len) in Attributes(b))
        {
            var a = b.Slice(off, len); uint type = U32(a, 0); bool resident = a[8] == 0;
            if (resident)
            {
                int vo = U16(a, 20), vl = (int)U32(a, 16); if (vo + vl > len) throw new InvalidDataException("Resident bounds"); var value = a.Slice(vo, vl);
                if (type == 0x30 && vl >= 66)
                {
                    int nl = value[64]; if (66 + nl * 2 > vl) throw new InvalidDataException("Filename bounds");
                    e.AddName(new(U64(value, 0), Encoding.Unicode.GetString(value.Slice(66, nl * 2)), value[65]));
                }
                if (type == 0x10 && vl >= 36) { e.Modified = I64(value, 8); e.Reparse = (U32(value, 32) & 0x400) != 0; }
                if (type == 0x80) e.Logical += vl; // resident content occupies the MFT, no separate clusters
            }
            else
            {
                if (type == 0x80 && I64(a, 16) == 0) e.Logical += I64(a, 48);
                // Count real runs, including named streams and directory indexes; sparse runs cost zero.
                foreach (var r in Runs(a)) if (r.Lcn >= 0) e.Allocated = checked(e.Allocated + r.Clusters * cluster);
            }
        }
        // Header reference count includes DOS names, which are not extra hard links.
        e.Links = e.CountLinks();
        return e;
    }
}
