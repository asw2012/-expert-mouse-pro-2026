using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExpertMouse;

internal sealed class UsbCaptureSession : IAsyncDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream? recording;
    private readonly UsbTarget target;
    private readonly UsbPcapDecoder decoder;
    private readonly Task worker;
    private Task? stopTask;
    private volatile bool stopping;
    private int queued;
    internal ConcurrentQueue<UsbReport> Reports { get; } = new();
    internal Exception? Failure { get; private set; }
    internal bool Ready => Volatile.Read(ref ready);
    private bool ready;
    internal long ReportCount => Interlocked.Read(ref reportCount);
    private long reportCount;

    internal UsbCaptureSession(UsbTarget target, string? pcapPath = null)
    {
        this.target = target;
        decoder = new(target.Bus, target.Address);
        UsbCapture.VerifyTarget(target);
        recording = pcapPath is null ? null :
            new FileStream(pcapPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            handle = UsbCapture.Open(target.CaptureDevice, 0xC0000000);
        }
        catch (Win32Exception)
        {
            recording?.Dispose();
            if (pcapPath is not null)
                File.Delete(pcapPath);
            throw;
        }
        try
        {
            UsbCapture.Control(handle, UsbCapture.SetSnapLength, BitConverter.GetBytes(65535u));
            UsbCapture.Control(handle, UsbCapture.SetupBuffer, BitConverter.GetBytes(1024u * 1024));
            UsbCapture.Control(handle, UsbCapture.StartFiltering, UsbCapture.AddressFilter(target.Address));
            worker = Task.Run(ReadLoop);
        }
        catch (Win32Exception)
        {
            handle.Dispose();
            recording?.Dispose();
            if (pcapPath is not null)
                File.Delete(pcapPath);
            throw;
        }
    }

    internal bool TryTake(out UsbReport? report)
    {
        if (!Reports.TryDequeue(out report))
            return false;
        Interlocked.Decrement(ref queued);
        return true;
    }

    private void ReadLoop()
    {
        try
        {
            var buffer = new byte[65536];
            while (!stopping)
            {
                if (!UsbCapture.ReadFile(handle, buffer, (uint)buffer.Length, out var read, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (stopping && error == 995)
                        break;
                    throw new Win32Exception(error, "Read USBPcap reports");
                }
                if (read == 0)
                    throw new InvalidOperationException("USBPcap ended the capture unexpectedly.");
                UsbCapture.VerifyTarget(target);
                var chunk = buffer.AsSpan(0, checked((int)read));
                var reports = decoder.Append(chunk);
                recording?.Write(chunk);
                if (!ready && decoder.HeaderRead)
                    recording?.Flush();
                Volatile.Write(ref ready, decoder.HeaderRead);
                foreach (var report in reports)
                {
                    if (Interlocked.Increment(ref queued) > 20000)
                        throw new InvalidOperationException("The USB capture window cannot keep up; repeat shorter tests.");
                    Reports.Enqueue(report);
                    Interlocked.Increment(ref reportCount);
                }
            }
            decoder.Complete();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Failure = ex;
        }
        finally
        {
            try
            {
                recording?.Flush();
            }
            catch (IOException ex)
            {
                Failure = ex;
            }
        }
    }

    internal void CheckHealth()
    {
        if (worker.IsCompleted)
            throw new InvalidOperationException("USB capture stopped. " +
                (Failure?.Message ?? "No completion reason was supplied."));
        UsbCapture.VerifyTarget(target);
    }

    public ValueTask DisposeAsync() => new(stopTask ??= StopAsync());

    private async Task StopAsync()
    {
        stopping = true;
        // Repeat cancellation to cover a read entering between the stop flag and CancelIoEx.
        while (!worker.IsCompleted)
        {
            if (!UsbCapture.CancelIoEx(handle, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1168)
                    throw new Win32Exception(error, "Cancel USB capture read");
            }
            await Task.WhenAny(worker, Task.Delay(50));
        }
        await worker;
        try
        {
            UsbCapture.Control(handle, UsbCapture.StopFiltering);
        }
        finally
        {
            handle.Dispose();
            recording?.Dispose();
        }
        if (Failure is not null)
            throw new InvalidOperationException($"USB capture failed: {Failure.Message}", Failure);
    }
}
