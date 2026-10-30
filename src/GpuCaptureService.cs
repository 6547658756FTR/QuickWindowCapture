using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickWindowCapture
{
    internal sealed class GpuCaptureResetRequiredException : InvalidOperationException
    {
        internal GpuCaptureResetRequiredException(string message)
            : base(message)
        {
        }
    }

    // DXGI Desktop Duplication keeps the latest desktop frame in GPU memory.
    // A hotkey press only copies the active-window rectangle back to the CPU.
    internal sealed class GpuCaptureService : IDisposable
    {
        private readonly List<OutputDuplicator> outputs = new List<OutputDuplicator>();

        internal bool IsAvailable { get; private set; }
        internal string UnavailableReason { get; private set; }

        internal GpuCaptureService()
        {
            Initialize();
        }

        internal Bitmap CaptureWindow(IntPtr windowHandle)
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException(UnavailableReason ?? "고속 캡처 엔진을 사용할 수 없습니다.");
            }

            Rectangle windowBounds = ScreenshotService.GetVisibleWindowBounds(windowHandle);
            Bitmap result = new Bitmap(windowBounds.Width, windowBounds.Height, PixelFormat.Format32bppArgb);
            int copiedArea = 0;

            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(Color.Black);
                }

                foreach (OutputDuplicator output in outputs)
                {
                    Rectangle intersection = Rectangle.Intersect(windowBounds, output.DesktopBounds);
                    if (intersection.Width <= 0 || intersection.Height <= 0)
                    {
                        continue;
                    }

                    Point destination = new Point(
                        intersection.Left - windowBounds.Left,
                        intersection.Top - windowBounds.Top);
                    output.CopyRegionTo(result, intersection, destination);
                    copiedArea += intersection.Width * intersection.Height;
                }

                if (copiedArea == 0)
                {
                    throw new InvalidOperationException("고속 캡처가 현재 창이 있는 모니터를 찾지 못했습니다.");
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void Initialize()
        {
            DxgiNative.IDXGIFactory1 factory = null;
            string lastError = null;

            try
            {
                Guid factoryId = typeof(DxgiNative.IDXGIFactory1).GUID;
                int result = DxgiNative.CreateDXGIFactory1(ref factoryId, out factory);
                DxgiNative.ThrowIfFailed(result, "그래픽 어댑터를 열지 못했습니다.");

                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    DxgiNative.IDXGIAdapter1 adapter;
                    result = factory.EnumAdapters1(adapterIndex, out adapter);
                    if (result == DxgiNative.DXGI_ERROR_NOT_FOUND)
                    {
                        break;
                    }

                    if (result < 0 || adapter == null)
                    {
                        lastError = "그래픽 어댑터를 열지 못했습니다.";
                        continue;
                    }

                    try
                    {
                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            DxgiNative.IDXGIOutput output;
                            result = adapter.EnumOutputs(outputIndex, out output);
                            if (result == DxgiNative.DXGI_ERROR_NOT_FOUND)
                            {
                                break;
                            }

                            if (result < 0 || output == null)
                            {
                                lastError = "모니터 출력을 열지 못했습니다.";
                                continue;
                            }

                            try
                            {
                                DxgiNative.OutputDescription description;
                                result = output.GetDesc(out description);
                                if (result < 0 || !description.AttachedToDesktop)
                                {
                                    continue;
                                }

                                if (description.Rotation != DxgiNative.ModeRotation.Identity &&
                                    description.Rotation != DxgiNative.ModeRotation.Unspecified)
                                {
                                    lastError = "회전된 모니터에서는 고속 모드를 사용할 수 없습니다.";
                                    continue;
                                }

                                DxgiNative.IDXGIOutput1 output1 = output as DxgiNative.IDXGIOutput1;
                                if (output1 == null)
                                {
                                    lastError = "현재 그래픽 드라이버가 고속 캡처를 지원하지 않습니다.";
                                    continue;
                                }

                                try
                                {
                                    outputs.Add(new OutputDuplicator(adapter, output1, description));
                                }
                                catch (Exception exception)
                                {
                                    lastError = exception.Message;
                                }
                            }
                            finally
                            {
                                ReleaseComObject(output);
                            }
                        }
                    }
                    finally
                    {
                        ReleaseComObject(adapter);
                    }
                }

                IsAvailable = outputs.Count > 0;
                UnavailableReason = IsAvailable
                    ? null
                    : (lastError ?? "사용 가능한 고속 캡처 모니터를 찾지 못했습니다.");
            }
            catch (Exception exception)
            {
                UnavailableReason = exception.Message;
                IsAvailable = false;
                DisposeOutputs();
            }
            finally
            {
                ReleaseComObject(factory);
            }
        }

        private void DisposeOutputs()
        {
            foreach (OutputDuplicator output in outputs)
            {
                output.Dispose();
            }
            outputs.Clear();
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.FinalReleaseComObject(value);
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            IsAvailable = false;
            DisposeOutputs();
        }

        private sealed class OutputDuplicator : IDisposable
        {
            private readonly object sync = new object();
            private readonly IntPtr device;
            private readonly IntPtr context;
            private readonly IntPtr duplication;
            private readonly DxgiNative.CreateTexture2DDelegate createTexture2D;
            private readonly DxgiNative.MapResourceDelegate mapResource;
            private readonly DxgiNative.UnmapResourceDelegate unmapResource;
            private readonly DxgiNative.CopySubresourceRegionDelegate copySubresourceRegion;
            private readonly DxgiNative.CopyResourceDelegate copyResource;
            private readonly DxgiNative.AcquireNextFrameDelegate acquireNextFrame;
            private readonly DxgiNative.ReleaseFrameDelegate releaseFrame;
            private readonly Thread updateThread;
            private IntPtr latestTexture;
            private IntPtr stagingTexture;
            private uint stagingWidth;
            private uint stagingHeight;
            private int stagingFormat;
            private volatile bool running;
            private bool hasFrame;
            private string failure;
            private bool resetRequired;
            private int disposeRequested;
            private int resourcesReleased;

            internal Rectangle DesktopBounds { get; private set; }

            internal OutputDuplicator(
                DxgiNative.IDXGIAdapter1 adapter,
                DxgiNative.IDXGIOutput1 output,
                DxgiNative.OutputDescription description)
            {
                DesktopBounds = Rectangle.FromLTRB(
                    description.DesktopCoordinates.Left,
                    description.DesktopCoordinates.Top,
                    description.DesktopCoordinates.Right,
                    description.DesktopCoordinates.Bottom);

                IntPtr adapterPointer = Marshal.GetIUnknownForObject(adapter);
                IntPtr duplicationPointer = IntPtr.Zero;
                try
                {
                    DxgiNative.FeatureLevel featureLevel;
                    int result = DxgiNative.D3D11CreateDevice(
                        adapterPointer,
                        DxgiNative.DriverType.Unknown,
                        IntPtr.Zero,
                        DxgiNative.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                        IntPtr.Zero,
                        0,
                        DxgiNative.D3D11_SDK_VERSION,
                        out device,
                        out featureLevel,
                        out context);
                    DxgiNative.ThrowIfFailed(result, "Direct3D 장치를 만들지 못했습니다.");

                    result = output.DuplicateOutput(device, out duplicationPointer);
                    DxgiNative.ThrowIfFailed(result, "그래픽 드라이버가 화면 복제를 허용하지 않았습니다.");

                    Guid duplicationId = typeof(DxgiNative.IDXGIOutputDuplication).GUID;
                    IntPtr typedPointer;
                    result = Marshal.QueryInterface(duplicationPointer, ref duplicationId, out typedPointer);
                    DxgiNative.ThrowIfFailed(result, "화면 복제 인터페이스를 열지 못했습니다.");
                    duplication = typedPointer;
                }
                catch
                {
                    if (context != IntPtr.Zero)
                    {
                        Marshal.Release(context);
                    }
                    if (device != IntPtr.Zero)
                    {
                        Marshal.Release(device);
                    }
                    throw;
                }
                finally
                {
                    if (duplicationPointer != IntPtr.Zero)
                    {
                        Marshal.Release(duplicationPointer);
                    }
                    Marshal.Release(adapterPointer);
                }

                createTexture2D = DxgiNative.GetComMethod<DxgiNative.CreateTexture2DDelegate>(device, 5);
                mapResource = DxgiNative.GetComMethod<DxgiNative.MapResourceDelegate>(context, 14);
                unmapResource = DxgiNative.GetComMethod<DxgiNative.UnmapResourceDelegate>(context, 15);
                copySubresourceRegion = DxgiNative.GetComMethod<DxgiNative.CopySubresourceRegionDelegate>(context, 46);
                copyResource = DxgiNative.GetComMethod<DxgiNative.CopyResourceDelegate>(context, 47);
                acquireNextFrame = DxgiNative.GetComMethod<DxgiNative.AcquireNextFrameDelegate>(duplication, 8);
                releaseFrame = DxgiNative.GetComMethod<DxgiNative.ReleaseFrameDelegate>(duplication, 14);

                running = true;
                updateThread = new Thread(UpdateLoop);
                updateThread.IsBackground = true;
                updateThread.Name = "QuickWindowCapture GPU Frame";
                updateThread.SetApartmentState(ApartmentState.MTA);
                updateThread.Start();
            }

            internal void CopyRegionTo(Bitmap destination, Rectangle desktopRegion, Point destinationPoint)
            {
                Rectangle relativeRegion = new Rectangle(
                    desktopRegion.Left - DesktopBounds.Left,
                    desktopRegion.Top - DesktopBounds.Top,
                    desktopRegion.Width,
                    desktopRegion.Height);

                lock (sync)
                {
                    if (!string.IsNullOrEmpty(failure))
                    {
                        if (resetRequired)
                        {
                            throw new GpuCaptureResetRequiredException(failure);
                        }

                        throw new InvalidOperationException(failure);
                    }

                    if (!hasFrame || latestTexture == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("고속 캡처의 첫 화면을 준비하는 중입니다. 잠시 후 다시 시도해 주세요.");
                    }

                    DxgiNative.Texture2DDescription sourceDescription;
                    GetTextureDescription(latestTexture, out sourceDescription);
                    DxgiNative.Texture2DDescription stagingDescription = sourceDescription;
                    stagingDescription.Width = (uint)relativeRegion.Width;
                    stagingDescription.Height = (uint)relativeRegion.Height;
                    stagingDescription.MipLevels = 1;
                    stagingDescription.ArraySize = 1;
                    stagingDescription.Usage = DxgiNative.ResourceUsage.Staging;
                    stagingDescription.BindFlags = 0;
                    stagingDescription.CpuAccessFlags = DxgiNative.D3D11_CPU_ACCESS_READ;
                    stagingDescription.MiscFlags = 0;

                    EnsureStagingTexture(ref stagingDescription);

                    DxgiNative.Box sourceBox = new DxgiNative.Box
                    {
                        Left = (uint)relativeRegion.Left,
                        Top = (uint)relativeRegion.Top,
                        Front = 0,
                        Right = (uint)relativeRegion.Right,
                        Bottom = (uint)relativeRegion.Bottom,
                        Back = 1
                    };

                    copySubresourceRegion(
                        context,
                        stagingTexture,
                        0,
                        0,
                        0,
                        0,
                        latestTexture,
                        0,
                        ref sourceBox);

                    DxgiNative.MappedSubresource mapped;
                    int result = mapResource(
                        context,
                        stagingTexture,
                        0,
                        DxgiNative.MapType.Read,
                        0,
                        out mapped);
                    DxgiNative.ThrowIfFailed(result, "GPU 화면을 읽지 못했습니다.");

                    try
                    {
                        CopyMappedPixels(destination, destinationPoint, relativeRegion.Size, mapped);
                    }
                    finally
                    {
                        unmapResource(context, stagingTexture, 0);
                    }
                }
            }

            private void EnsureStagingTexture(ref DxgiNative.Texture2DDescription description)
            {
                if (stagingTexture != IntPtr.Zero &&
                    stagingWidth == description.Width &&
                    stagingHeight == description.Height &&
                    stagingFormat == description.Format)
                {
                    return;
                }

                if (stagingTexture != IntPtr.Zero)
                {
                    Marshal.Release(stagingTexture);
                    stagingTexture = IntPtr.Zero;
                }

                int result = createTexture2D(device, ref description, IntPtr.Zero, out stagingTexture);
                DxgiNative.ThrowIfFailed(result, "고속 캡처용 메모리를 만들지 못했습니다.");
                stagingWidth = description.Width;
                stagingHeight = description.Height;
                stagingFormat = description.Format;
            }

            private static void CopyMappedPixels(
                Bitmap destination,
                Point destinationPoint,
                Size size,
                DxgiNative.MappedSubresource mapped)
            {
                Rectangle lockRectangle = new Rectangle(0, 0, destination.Width, destination.Height);
                BitmapData data = destination.LockBits(
                    lockRectangle,
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    int bytesPerRow = size.Width * 4;
                    for (int row = 0; row < size.Height; row++)
                    {
                        IntPtr source = IntPtr.Add(mapped.Data, row * (int)mapped.RowPitch);
                        IntPtr target = IntPtr.Add(
                            data.Scan0,
                            (destinationPoint.Y + row) * data.Stride + destinationPoint.X * 4);
                        DxgiNative.CopyMemory(target, source, new UIntPtr((uint)bytesPerRow));
                    }
                }
                finally
                {
                    destination.UnlockBits(data);
                }
            }

            private void UpdateLoop()
            {
                try
                {
                    while (running)
                    {
                        DxgiNative.FrameInformation frameInformation;
                        IntPtr desktopResource = IntPtr.Zero;
                        int result = acquireNextFrame(duplication, 100, out frameInformation, out desktopResource);

                        if (result == DxgiNative.DXGI_ERROR_WAIT_TIMEOUT)
                        {
                            continue;
                        }

                        if (result < 0)
                        {
                            lock (sync)
                            {
                                resetRequired = true;
                                failure = result == DxgiNative.DXGI_ERROR_ACCESS_LOST
                                    ? "화면 모드가 변경되어 고속 캡처 연결이 끊겼습니다."
                                    : "고속 캡처 프레임을 받지 못했습니다.";
                            }
                            break;
                        }

                        IntPtr acquiredTexture = IntPtr.Zero;
                        try
                        {
                            Guid textureId = typeof(DxgiNative.ID3D11Texture2D).GUID;
                            result = Marshal.QueryInterface(desktopResource, ref textureId, out acquiredTexture);
                            DxgiNative.ThrowIfFailed(result, "GPU 화면 형식을 읽지 못했습니다.");

                            lock (sync)
                            {
                                EnsureLatestTexture(acquiredTexture);
                                copyResource(context, latestTexture, acquiredTexture);
                                hasFrame = true;
                            }
                        }
                        catch (Exception exception)
                        {
                            lock (sync)
                            {
                                resetRequired = true;
                                failure = exception.Message;
                            }
                            break;
                        }
                        finally
                        {
                            releaseFrame(duplication);
                            if (desktopResource != IntPtr.Zero)
                            {
                                Marshal.Release(desktopResource);
                            }
                            if (acquiredTexture != IntPtr.Zero)
                            {
                                Marshal.Release(acquiredTexture);
                            }
                        }

                        // Avoid consuming unnecessary GPU time on very high-refresh displays.
                        Thread.Sleep(12);
                    }
                }
                finally
                {
                    if (Interlocked.CompareExchange(ref disposeRequested, 0, 0) != 0)
                    {
                        ReleaseResources();
                    }
                }
            }

            private static void GetTextureDescription(IntPtr texture, out DxgiNative.Texture2DDescription description)
            {
                DxgiNative.GetTextureDescriptionDelegate getDescription =
                    DxgiNative.GetComMethod<DxgiNative.GetTextureDescriptionDelegate>(texture, 10);
                getDescription(texture, out description);
            }

            private void EnsureLatestTexture(IntPtr source)
            {
                DxgiNative.Texture2DDescription description;
                GetTextureDescription(source, out description);

                if (latestTexture != IntPtr.Zero)
                {
                    DxgiNative.Texture2DDescription current;
                    GetTextureDescription(latestTexture, out current);
                    if (current.Width == description.Width &&
                        current.Height == description.Height &&
                        current.Format == description.Format)
                    {
                        return;
                    }

                    Marshal.Release(latestTexture);
                    latestTexture = IntPtr.Zero;
                }

                description.MipLevels = 1;
                description.ArraySize = 1;
                description.Usage = DxgiNative.ResourceUsage.Default;
                description.BindFlags = 0;
                description.CpuAccessFlags = 0;
                description.MiscFlags = 0;

                int result = createTexture2D(device, ref description, IntPtr.Zero, out latestTexture);
                DxgiNative.ThrowIfFailed(result, "최신 GPU 화면을 보관할 수 없습니다.");
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref disposeRequested, 1);
                running = false;
                if (updateThread != null && updateThread.IsAlive)
                {
                    updateThread.Join(350);
                }

                if (updateThread != null && updateThread.IsAlive)
                {
                    // A wedged graphics driver must not be given COM pointers that
                    // another thread has already released. The update thread will
                    // release them itself if it eventually returns.
                    DiagnosticLog.Write("GPU 갱신 스레드 종료가 지연되어 리소스 해제를 보류했습니다.");
                    return;
                }

                ReleaseResources();
            }

            private void ReleaseResources()
            {
                if (Interlocked.Exchange(ref resourcesReleased, 1) != 0)
                {
                    return;
                }

                lock (sync)
                {
                    if (latestTexture != IntPtr.Zero)
                    {
                        Marshal.Release(latestTexture);
                        latestTexture = IntPtr.Zero;
                    }
                    if (stagingTexture != IntPtr.Zero)
                    {
                        Marshal.Release(stagingTexture);
                        stagingTexture = IntPtr.Zero;
                    }
                    if (duplication != IntPtr.Zero)
                    {
                        Marshal.Release(duplication);
                    }
                    if (context != IntPtr.Zero)
                    {
                        Marshal.Release(context);
                    }
                    if (device != IntPtr.Zero)
                    {
                        Marshal.Release(device);
                    }
                }
            }
        }
    }

    internal static class DxgiNative
    {
        internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        internal const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        internal const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        internal const uint D3D11_CPU_ACCESS_READ = 0x20000;
        internal const uint D3D11_SDK_VERSION = 7;

        internal enum DriverType
        {
            Unknown = 0
        }

        internal enum FeatureLevel
        {
            Level91 = 0x9100,
            Level92 = 0x9200,
            Level93 = 0x9300,
            Level100 = 0xA000,
            Level101 = 0xA100,
            Level110 = 0xB000,
            Level111 = 0xB100,
            Level120 = 0xC000,
            Level121 = 0xC100
        }

        internal enum ModeRotation
        {
            Unspecified = 0,
            Identity = 1,
            Rotate90 = 2,
            Rotate180 = 3,
            Rotate270 = 4
        }

        internal enum ResourceUsage
        {
            Default = 0,
            Immutable = 1,
            Dynamic = 2,
            Staging = 3
        }

        internal enum MapType
        {
            Read = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SampleDescription
        {
            internal uint Count;
            internal uint Quality;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Texture2DDescription
        {
            internal uint Width;
            internal uint Height;
            internal uint MipLevels;
            internal uint ArraySize;
            internal int Format;
            internal SampleDescription SampleDescription;
            internal ResourceUsage Usage;
            internal uint BindFlags;
            internal uint CpuAccessFlags;
            internal uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Box
        {
            internal uint Left;
            internal uint Top;
            internal uint Front;
            internal uint Right;
            internal uint Bottom;
            internal uint Back;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MappedSubresource
        {
            internal IntPtr Data;
            internal uint RowPitch;
            internal uint DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PointerPosition
        {
            internal NativePoint Position;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool Visible;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FrameInformation
        {
            internal long LastPresentTime;
            internal long LastMouseUpdateTime;
            internal uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool RectanglesCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool ProtectedContentMaskedOut;
            internal PointerPosition PointerPosition;
            internal uint TotalMetadataBufferSize;
            internal uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
            internal NativeMethods.Rect DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool AttachedToDesktop;
            internal ModeRotation Rotation;
            internal IntPtr Monitor;
        }

        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        [DllImport("d3d11.dll")]
        internal static extern int D3D11CreateDevice(
            IntPtr adapter,
            DriverType driverType,
            IntPtr software,
            uint flags,
            IntPtr featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out IntPtr device,
            out FeatureLevel selectedFeatureLevel,
            out IntPtr immediateContext);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        internal static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);

        internal static void ThrowIfFailed(int result, string message)
        {
            if (result < 0)
            {
                throw new InvalidOperationException(message + " (0x" + result.ToString("X8") + ")");
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateTexture2DDelegate(
            IntPtr instance,
            ref Texture2DDescription description,
            IntPtr initialData,
            out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void GetTextureDescriptionDelegate(
            IntPtr instance,
            out Texture2DDescription description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int MapResourceDelegate(
            IntPtr instance,
            IntPtr resource,
            uint subresource,
            MapType mapType,
            uint mapFlags,
            out MappedSubresource mappedResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void UnmapResourceDelegate(
            IntPtr instance,
            IntPtr resource,
            uint subresource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void CopySubresourceRegionDelegate(
            IntPtr instance,
            IntPtr destination,
            uint destinationSubresource,
            uint destinationX,
            uint destinationY,
            uint destinationZ,
            IntPtr source,
            uint sourceSubresource,
            ref Box sourceBox);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void CopyResourceDelegate(
            IntPtr instance,
            IntPtr destination,
            IntPtr source);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int AcquireNextFrameDelegate(
            IntPtr instance,
            uint timeoutMilliseconds,
            out FrameInformation frameInformation,
            out IntPtr desktopResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int ReleaseFrameDelegate(IntPtr instance);

        internal static T GetComMethod<T>(IntPtr instance, int methodIndex) where T : class
        {
            IntPtr virtualTable = Marshal.ReadIntPtr(instance);
            IntPtr method = Marshal.ReadIntPtr(virtualTable, methodIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(method, typeof(T)) as T;
        }

        [ComImport]
        [Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIOutput
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid interfaceId, out IntPtr parent);
            [PreserveSig] int GetDesc(out OutputDescription description);
            void GetDisplayModeList();
            void FindClosestMatchingMode();
            void WaitForVBlank();
            void TakeOwnership();
            void ReleaseOwnership();
            void GetGammaControlCapabilities();
            void SetGammaControl();
            void GetGammaControl();
            void SetDisplaySurface();
            void GetDisplaySurfaceData();
            void GetFrameStatistics();
        }

        [ComImport]
        [Guid("00cddea8-939b-4b83-a340-a685226666cc")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIOutput1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid interfaceId, out IntPtr parent);
            [PreserveSig] int GetDesc(out OutputDescription description);
            void GetDisplayModeList();
            void FindClosestMatchingMode();
            void WaitForVBlank();
            void TakeOwnership();
            void ReleaseOwnership();
            void GetGammaControlCapabilities();
            void SetGammaControl();
            void GetGammaControl();
            void SetDisplaySurface();
            void GetDisplaySurfaceData();
            void GetFrameStatistics();
            void GetDisplayModeList1();
            void FindClosestMatchingMode1();
            void GetDisplaySurfaceData1();
            [PreserveSig]
            int DuplicateOutput(
                IntPtr device,
                out IntPtr duplication);
        }

        [ComImport]
        [Guid("29038f61-3839-4626-91fd-086879011a05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid interfaceId, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint outputIndex, [MarshalAs(UnmanagedType.Interface)] out IDXGIOutput output);
            void GetDesc();
            void CheckInterfaceSupport();
            void GetDesc1();
        }

        [ComImport]
        [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid interfaceId, out IntPtr parent);
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint adapterIndex, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport]
        [Guid("191cfac3-a341-470d-b26e-a864f428319c")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIOutputDuplication
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid interfaceId, out IntPtr parent);
            void GetDesc();
            [PreserveSig] int AcquireNextFrame(uint timeoutMilliseconds, out FrameInformation frameInformation, out IntPtr desktopResource);
            void GetFrameDirtyRects();
            void GetFrameMoveRects();
            void GetFramePointerShape();
            void MapDesktopSurface();
            void UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }

        [ComImport]
        [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3D11Device
        {
            void CreateBuffer();
            void CreateTexture1D();
            [PreserveSig] int CreateTexture2D(ref Texture2DDescription description, IntPtr initialData, [MarshalAs(UnmanagedType.Interface)] out ID3D11Texture2D texture);
        }

        [ComImport]
        [Guid("1841e5c8-16b0-489b-bcc8-44cfb0d5deae")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3D11DeviceChild
        {
            void GetDevice();
            void GetPrivateData();
            void SetPrivateData();
            void SetPrivateDataInterface();
        }

        [ComImport]
        [Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3D11Resource
        {
            void GetDevice();
            void GetPrivateData();
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetTypeInfo();
            void SetEvictionPriority();
            void GetEvictionPriority();
        }

        [ComImport]
        [Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3D11Texture2D
        {
            void GetDevice();
            void GetPrivateData();
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetTypeInfo();
            void SetEvictionPriority();
            void GetEvictionPriority();
            void GetDesc(out Texture2DDescription description);
        }

        [ComImport]
        [Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3D11DeviceContext
        {
            void GetDevice();
            void GetPrivateData();
            void SetPrivateData();
            void SetPrivateDataInterface();
            void VSSetConstantBuffers();
            void PSSetShaderResources();
            void PSSetShader();
            void PSSetSamplers();
            void VSSetShader();
            void DrawIndexed();
            void Draw();
            [PreserveSig] int Map([MarshalAs(UnmanagedType.IUnknown)] object resource, uint subresource, MapType mapType, uint mapFlags, out MappedSubresource mappedResource);
            void Unmap([MarshalAs(UnmanagedType.IUnknown)] object resource, uint subresource);
            void PSSetConstantBuffers();
            void IASetInputLayout();
            void IASetVertexBuffers();
            void IASetIndexBuffer();
            void DrawIndexedInstanced();
            void DrawInstanced();
            void GSSetConstantBuffers();
            void GSSetShader();
            void IASetPrimitiveTopology();
            void VSSetShaderResources();
            void VSSetSamplers();
            void Begin();
            void End();
            void GetData();
            void SetPredication();
            void GSSetShaderResources();
            void GSSetSamplers();
            void OMSetRenderTargets();
            void OMSetRenderTargetsAndUnorderedAccessViews();
            void OMSetBlendState();
            void OMSetDepthStencilState();
            void SOSetTargets();
            void DrawAuto();
            void DrawIndexedInstancedIndirect();
            void DrawInstancedIndirect();
            void Dispatch();
            void DispatchIndirect();
            void RSSetState();
            void RSSetViewports();
            void RSSetScissorRects();
            void CopySubresourceRegion(
                [MarshalAs(UnmanagedType.IUnknown)] object destination,
                uint destinationSubresource,
                uint destinationX,
                uint destinationY,
                uint destinationZ,
                [MarshalAs(UnmanagedType.IUnknown)] object source,
                uint sourceSubresource,
                ref Box sourceBox);
            void CopyResource(
                [MarshalAs(UnmanagedType.IUnknown)] object destination,
                [MarshalAs(UnmanagedType.IUnknown)] object source);
        }
    }
}
