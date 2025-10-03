using LibKenjinx.Android;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LibKenjinx
{
    public static partial class LibKenjinx
    {
        private static bool _isActive;
        private static bool _isStopped;
        private static CancellationTokenSource? _gpuCancellationTokenSource;
        private static SwapBuffersCallback? _swapBuffersCallback;
        private static NativeGraphicsInterop _nativeGraphicsInterop;
        private static ManualResetEvent? _gpuDoneEvent;
        private static bool _enableGraphicsLogging;

        public delegate void SwapBuffersCallback();
        public delegate IntPtr GetProcAddress(string name);
        public delegate IntPtr CreateSurface(IntPtr instance);

        public static IRenderer? Renderer { get; set; }
        public static GraphicsConfiguration GraphicsConfiguration { get; private set; }

        public static bool InitializeGraphics(GraphicsConfiguration graphicsConfiguration)
        {
            GraphicsConfig.ResScale = graphicsConfiguration.ResScale;
            GraphicsConfig.MaxAnisotropy = graphicsConfiguration.MaxAnisotropy;
            GraphicsConfig.FastGpuTime = graphicsConfiguration.FastGpuTime;
            GraphicsConfig.Fast2DCopy = graphicsConfiguration.Fast2DCopy;
            GraphicsConfig.EnableMacroJit = graphicsConfiguration.EnableMacroJit;
            GraphicsConfig.EnableMacroHLE = graphicsConfiguration.EnableMacroHLE;
            GraphicsConfig.EnableShaderCache = graphicsConfiguration.EnableShaderCache;
            GraphicsConfig.EnableTextureRecompression = graphicsConfiguration.EnableTextureRecompression;

            GraphicsConfiguration = graphicsConfiguration;

            return true;
        }

        public static bool InitializeGraphicsRenderer(GraphicsBackend graphicsBackend, CreateSurface? createSurfaceFunc, string?[] requiredExtensions)
        {
            if (Renderer != null)
            {
                return false;
            }

            if (graphicsBackend == GraphicsBackend.OpenGl)
            {
                Renderer = new OpenGLRenderer();
            }
            else if (graphicsBackend == GraphicsBackend.Vulkan)
            {
                Renderer = new VulkanRenderer(Vk.GetApi(), (instance, vk) => new SurfaceKHR(createSurfaceFunc == null ? null : (ulong?)createSurfaceFunc(instance.Handle)),
                    () => requiredExtensions,
                    null);
            }
            else
            {
                return false;
            }

            return true;
        }

        public static void SetRendererSize(int width, int height)
        {
            Renderer?.Window?.SetSize(width, height);
        }

        public static void SetVsyncState(Ryujinx.Common.Configuration.VSyncMode vSyncMode)
        {
            var device = SwitchDevice!.EmulationContext!;
            device.VSyncMode = vSyncMode;
            device.Gpu.Renderer.Window.ChangeVSyncMode((Ryujinx.Graphics.GAL.VSyncMode)vSyncMode);
        }

        public static void RunLoop()
        {
            if (Renderer == null)
            {
                return;
            }
            ARMeilleure.Optimizations.EcoFriendly = SwitchDevice!.EnableLowPowerPtc;
            ARMeilleure.Optimizations.CacheEviction = SwitchDevice.EnableJitCacheEviction;

            var device = SwitchDevice.EmulationContext!;
            _gpuDoneEvent = new ManualResetEvent(true);

            device.Gpu.Renderer.Initialize(_enableGraphicsLogging ? GraphicsDebugLevel.All : GraphicsDebugLevel.None);

            _gpuCancellationTokenSource = new CancellationTokenSource();

            device.Gpu.ShaderCacheStateChanged += LoadProgressStateChangedHandler;
            device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += LoadProgressStateChangedHandler;

            try
            {
                device.Gpu.Renderer.RunLoop(() =>
                {
                    _gpuDoneEvent.Reset();
                    device.Gpu.SetGpuThread();
                    device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                    _isActive = true;

                    if (PlatformInfo.IsBionic)
                    {
                        setRenderingThread();
                    }

                    while (_isActive)
                    {
                        if (_isStopped)
                        {
                            break;
                        }

                        if (device.WaitFifo())
                        {
                            device.Statistics.RecordFifoStart();
                            device.ProcessFrame();
                            device.Statistics.RecordFifoEnd();
                        }

                        while (device.ConsumeFrameAvailable())
                        {
                            device.PresentFrame(() =>
                            {
                                if (device.Gpu.Renderer is ThreadedRenderer threaded && threaded.BaseRenderer is VulkanRenderer vulkanRenderer)
                                {
                                    setCurrentTransform(_window, (int)vulkanRenderer.CurrentTransform);
                                }
                                _swapBuffersCallback?.Invoke();
                            });
                        }
                    }

                    if (device.Gpu.Renderer is ThreadedRenderer threaded)
                    {
                        threaded.FlushThreadedCommands();
                    }

                    _gpuDoneEvent.Set();
                });
            }
            finally
            {
                device.Gpu.ShaderCacheStateChanged -= LoadProgressStateChangedHandler;
                device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= LoadProgressStateChangedHandler;
            }
        }

        private static void LoadProgressStateChangedHandler<T>(T state, int current, int total) where T : Enum
        {
            void SetInfo(string status, float value)
            {
                if(PlatformInfo.IsBionic)
                {
                    Interop.UpdateProgress(status, value);
                }
            }
            var status = $"{current} / {total}";
            var progress = current / (float)total;
            if (float.IsNaN(progress))
                progress = 0;

            switch (state)
            {
                case LoadState ptcState:
                    if (float.IsNaN((progress)))
                        progress = 0;

                    switch (ptcState)
                    {
                        case LoadState.Unloaded:
                        case LoadState.Loading:
                            SetInfo($"翻译缓存加载中 {status}", progress);
                            break;
                        case LoadState.Loaded:
                            SetInfo($"翻译缓存已加载", -1);
                            break;
                    }
                    break;
                case ShaderCacheState shaderCacheState:
                    switch (shaderCacheState)
                    {
                        case ShaderCacheState.Start:
                        case ShaderCacheState.Loading:
                            SetInfo($"着色器编译中 {status}", progress);
                            break;
                        case ShaderCacheState.Packaging:
                            SetInfo($"着色器打包中 {status}", progress);
                            break;
                        case ShaderCacheState.Loaded:
                            SetInfo($"着色器已加载", -1);
                            break;
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}");
            }
        }

        public static void SetSwapBuffersCallback(SwapBuffersCallback swapBuffersCallback)
        {
            _swapBuffersCallback = swapBuffersCallback;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsConfiguration
    {
        public float ResScale;
        public float MaxAnisotropy;
        public bool FastGpuTime;
        public bool Fast2DCopy;
        public bool EnableMacroJit;
        public bool EnableMacroHLE;
        public bool EnableShaderCache;
        public bool EnableTextureRecompression;
        public BackendThreading BackendThreading;
        public AspectRatio AspectRatio; // <- important

        public GraphicsConfiguration()
        {
            ResScale = 1f;
            MaxAnisotropy = -1;
            FastGpuTime = true;
            Fast2DCopy = true;
            EnableMacroJit = false;
            EnableMacroHLE = true;
            EnableShaderCache = true;
            EnableTextureRecompression = false;
            BackendThreading = BackendThreading.Auto;
            AspectRatio = AspectRatio.Fixed16x9;
        }
    }

    public struct NativeGraphicsInterop
    {
        public IntPtr GlGetProcAddress;
        public IntPtr VkNativeContextLoader;
        public IntPtr VkCreateSurface;
        public IntPtr VkRequiredExtensions;
        public int VkRequiredExtensionsCount;
    }
}
