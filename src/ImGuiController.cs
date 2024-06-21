// ImGuiController with docking and viewport support for MoonWorks/Refresh.
// Based on the example im ImGui.NET and MoonWorksDearImGuiScaffold.
// One change is needed in MoonWorks, and that is to make Window.Handle a public getter.

using ImGuiNET;
using MoonWorks;
using MoonWorks.Graphics;
using MoonWorks.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MoonWorksDearImGuiScaffold;

internal class ImGuiController : IDisposable
{
    public event Action OnGui;

    private readonly string shaderContentPath = Path.Combine(System.AppContext.BaseDirectory, "Content", "Shaders");

    private readonly GraphicsDevice graphicsDevice;
    private readonly Window mainWindow;
    private readonly Inputs inputs;
    private readonly Color clearColor;

    private readonly Platform_CreateWindow createWindow;
    private readonly Platform_DestroyWindow destroyWindow;
    private readonly Platform_GetWindowPos getWindowPos;
    private readonly Platform_ShowWindow showWindow;
    private readonly Platform_SetWindowPos setWindowPos;
    private readonly Platform_SetWindowSize setWindowSize;
    private readonly Platform_GetWindowSize getWindowSize;
    private readonly Platform_SetWindowFocus setWindowFocus;
    private readonly Platform_GetWindowFocus getWindowFocus;
    private readonly Platform_GetWindowMinimized getWindowMinimized;
    private readonly Platform_SetWindowTitle setWindowTitle;

    private readonly GraphicsPipeline imGuiPipeline;
    private readonly ShaderModule imGuiVertexShader;
    private readonly ShaderModule imGuiFragmentShader;
    private readonly Sampler imGuiSampler;
    private readonly TextureStorage textureStorage = new TextureStorage();
    private readonly Dictionary<Window, GCHandle> windows = new Dictionary<Window, GCHandle>(16);

    private Texture fontTexture = null;
    private uint vertexCount = 0;
    private uint indexCount = 0;
    private MoonWorks.Graphics.Buffer imGuiVertexBuffer = null;
    private MoonWorks.Graphics.Buffer imGuiIndexBuffer = null;
    private bool frameBegun = false;

    public ImGuiController(GraphicsDevice graphicsDevice, Window mainWindow, Inputs inputs, Color clearColor, ImGuiConfigFlags configFlags = ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.ViewportsEnable)
    {
        this.mainWindow = mainWindow;
        this.graphicsDevice = graphicsDevice;
        this.inputs = inputs;
        this.clearColor = clearColor;

        ImGui.CreateContext();

        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(mainWindow.Width, mainWindow.Height);
        io.DisplayFramebufferScale = Vector2.One;

        imGuiVertexShader = new ShaderModule(graphicsDevice, Path.Combine(shaderContentPath, "ImGui.vert.refresh"));
        imGuiFragmentShader = new ShaderModule(graphicsDevice, Path.Combine(shaderContentPath, "ImGui.frag.refresh"));

        imGuiSampler = new Sampler(graphicsDevice, SamplerCreateInfo.LinearClamp);

        imGuiPipeline = new GraphicsPipeline(
            graphicsDevice,
            new GraphicsPipelineCreateInfo
            {
                AttachmentInfo = new GraphicsPipelineAttachmentInfo(
                    new ColorAttachmentDescription(
                        mainWindow.SwapchainFormat,
                        ColorAttachmentBlendState.NonPremultiplied
                    )
                ),
                DepthStencilState = DepthStencilState.Disable,
                VertexShaderInfo = GraphicsShaderInfo.Create<Matrix4x4>(imGuiVertexShader, "main", 0),
                FragmentShaderInfo = GraphicsShaderInfo.Create(imGuiFragmentShader, "main", 1),
                VertexInputState = VertexInputState.CreateSingleBinding<Position2DTextureColorVertex>(),
                PrimitiveType = PrimitiveType.TriangleList,
                RasterizerState = RasterizerState.CW_CullNone,
                MultisampleState = MultisampleState.None
            }
        );

        BuildFontAtlas();

        Inputs.TextInput += c =>
        {
            if (c == '\t') { return; }
            io.AddInputCharacter(c);
        };

        io.ConfigFlags = configFlags;

        io.MouseDrawCursor = true;

        if (!OperatingSystem.IsWindows())
        {
            io.SetClipboardTextFn = Clipboard.SetFnPtr;
            io.GetClipboardTextFn = Clipboard.GetFnPtr;
        }

        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        ImGuiViewportPtr mainViewport = platformIO.Viewports[0];
        mainViewport.PlatformHandle = mainWindow.Handle;
        GCHandle handle = GCHandle.Alloc(mainWindow);
        mainViewport.PlatformUserData = (IntPtr)handle;
        windows.Add(mainWindow, handle);

        unsafe
        {
            createWindow = CreateWindow;
            destroyWindow = DestroyWindow;
            getWindowPos = GetWindowPos;
            showWindow = ShowWindow;
            setWindowPos = SetWindowPos;
            setWindowSize = SetWindowSize;
            getWindowSize = GetWindowSize;
            setWindowFocus = SetWindowFocus;
            getWindowFocus = GetWindowFocus;
            getWindowMinimized = GetWindowMinimized;
            setWindowTitle = SetWindowTitle;

            platformIO.Platform_CreateWindow = Marshal.GetFunctionPointerForDelegate(createWindow);
            platformIO.Platform_DestroyWindow = Marshal.GetFunctionPointerForDelegate(destroyWindow);
            platformIO.Platform_ShowWindow = Marshal.GetFunctionPointerForDelegate(showWindow);
            platformIO.Platform_SetWindowPos = Marshal.GetFunctionPointerForDelegate(setWindowPos);
            platformIO.Platform_SetWindowSize = Marshal.GetFunctionPointerForDelegate(setWindowSize);
            platformIO.Platform_SetWindowFocus = Marshal.GetFunctionPointerForDelegate(setWindowFocus);
            platformIO.Platform_GetWindowFocus = Marshal.GetFunctionPointerForDelegate(getWindowFocus);
            platformIO.Platform_GetWindowMinimized = Marshal.GetFunctionPointerForDelegate(getWindowMinimized);
            platformIO.Platform_SetWindowTitle = Marshal.GetFunctionPointerForDelegate(setWindowTitle);

            ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowPos(platformIO.NativePtr, Marshal.GetFunctionPointerForDelegate(getWindowPos));
            ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowSize(platformIO.NativePtr, Marshal.GetFunctionPointerForDelegate(getWindowSize));
        }

        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;
    }

    public void Update(float deltaTime)
    {
        if (frameBegun)
        {
            ImGui.Render();
            ImGui.UpdatePlatformWindows();
        }

        UpdatePerFrameImGuiData(deltaTime);
        UpdateInput();
        UpdateMonitors();

        frameBegun = true;
        ImGui.NewFrame();

        OnGui?.Invoke();

        ImGui.EndFrame();
    }

    private void UpdatePerFrameImGuiData(float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(mainWindow.Width, mainWindow.Height);
        io.DisplayFramebufferScale = new Vector2(1, 1);
        io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
    }

    private void UpdateInput()
    {
        ImGuiIOPtr io = ImGui.GetIO();

        if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        {
            // For viewports we use the global mouse position.
            _ = SDL2.SDL.SDL_GetGlobalMouseState(out int x, out int y);
            io.MousePos = new Vector2(x, y);
        }
        else
        {
            // Without viewports we need to use the relative position.
            //_ = SDL2.SDL.SDL_GetMouseState(out int x, out int y);
            io.MousePos = new Vector2(inputs.Mouse.X, inputs.Mouse.Y);
        }

        io.MouseDown[0] = inputs.Mouse.LeftButton.IsDown;
        io.MouseDown[1] = inputs.Mouse.RightButton.IsDown;
        io.MouseDown[2] = inputs.Mouse.MiddleButton.IsDown;
        io.MouseWheel = inputs.Mouse.Wheel;

        io.AddKeyEvent(ImGuiKey.A, inputs.Keyboard.IsDown(KeyCode.A));
        io.AddKeyEvent(ImGuiKey.Z, inputs.Keyboard.IsDown(KeyCode.Z));
        io.AddKeyEvent(ImGuiKey.Y, inputs.Keyboard.IsDown(KeyCode.Y));
        io.AddKeyEvent(ImGuiKey.X, inputs.Keyboard.IsDown(KeyCode.X));
        io.AddKeyEvent(ImGuiKey.C, inputs.Keyboard.IsDown(KeyCode.C));
        io.AddKeyEvent(ImGuiKey.V, inputs.Keyboard.IsDown(KeyCode.V));

        io.AddKeyEvent(ImGuiKey.Tab, inputs.Keyboard.IsDown(KeyCode.Tab));
        io.AddKeyEvent(ImGuiKey.LeftArrow, inputs.Keyboard.IsDown(KeyCode.Left));
        io.AddKeyEvent(ImGuiKey.RightArrow, inputs.Keyboard.IsDown(KeyCode.Right));
        io.AddKeyEvent(ImGuiKey.UpArrow, inputs.Keyboard.IsDown(KeyCode.Up));
        io.AddKeyEvent(ImGuiKey.DownArrow, inputs.Keyboard.IsDown(KeyCode.Down));
        io.AddKeyEvent(ImGuiKey.Enter, inputs.Keyboard.IsDown(KeyCode.Return));
        io.AddKeyEvent(ImGuiKey.Escape, inputs.Keyboard.IsDown(KeyCode.Escape));
        io.AddKeyEvent(ImGuiKey.Delete, inputs.Keyboard.IsDown(KeyCode.Delete));
        io.AddKeyEvent(ImGuiKey.Backspace, inputs.Keyboard.IsDown(KeyCode.Backspace));
        io.AddKeyEvent(ImGuiKey.Home, inputs.Keyboard.IsDown(KeyCode.Home));
        io.AddKeyEvent(ImGuiKey.End, inputs.Keyboard.IsDown(KeyCode.End));
        io.AddKeyEvent(ImGuiKey.PageDown, inputs.Keyboard.IsDown(KeyCode.PageDown));
        io.AddKeyEvent(ImGuiKey.PageUp, inputs.Keyboard.IsDown(KeyCode.PageUp));
        io.AddKeyEvent(ImGuiKey.Insert, inputs.Keyboard.IsDown(KeyCode.Insert));

        io.AddKeyEvent(ImGuiKey.ModCtrl, inputs.Keyboard.IsDown(KeyCode.LeftControl) || inputs.Keyboard.IsDown(KeyCode.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, inputs.Keyboard.IsDown(KeyCode.LeftShift) || inputs.Keyboard.IsDown(KeyCode.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, inputs.Keyboard.IsDown(KeyCode.LeftAlt) || inputs.Keyboard.IsDown(KeyCode.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModSuper, inputs.Keyboard.IsDown(KeyCode.LeftMeta) || inputs.Keyboard.IsDown(KeyCode.RightMeta));
    }

    private unsafe void UpdateMonitors()
    {
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        Marshal.FreeHGlobal(platformIO.NativePtr->Monitors.Data);
        int videoDisplayCount = SDL2.SDL.SDL_GetNumVideoDisplays();
        IntPtr data = Marshal.AllocHGlobal(Unsafe.SizeOf<ImGuiPlatformMonitor>() * videoDisplayCount);
        platformIO.NativePtr->Monitors = new ImVector(videoDisplayCount, videoDisplayCount, data);

        for (int i = 0; i < videoDisplayCount; i++)
        {
            _ = SDL2.SDL.SDL_GetDisplayUsableBounds(i, out SDL2.SDL.SDL_Rect usableBounds);
            _ = SDL2.SDL.SDL_GetDisplayBounds(i, out SDL2.SDL.SDL_Rect bounds);
            _ = SDL2.SDL.SDL_GetDisplayDPI(i, out float ddpi, out float hdpi, out float vdpi);
            ImGuiPlatformMonitorPtr monitor = platformIO.Monitors[i];
            float standardDpi = 96f; // Standard DPI typically used
            monitor.DpiScale = hdpi / standardDpi;
            monitor.MainPos = new Vector2(bounds.x, bounds.y);
            monitor.MainSize = new Vector2(bounds.w, bounds.h);
            monitor.WorkPos = new Vector2(usableBounds.x, usableBounds.y);
            monitor.WorkSize = new Vector2(usableBounds.w, usableBounds.h);
        }
    }

    public void Render()
    {
        if (!frameBegun)
        {
            return;
        }

        frameBegun = false;

        if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        {
            ImGui.Render();
            ImGui.UpdatePlatformWindows();
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();

            for (int i = 0; i < platformIO.Viewports.Size; i++)
            {
                ImGuiViewportPtr vp = platformIO.Viewports[i];
                Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;

                if (!window.Claimed)
                {
                    continue;
                }

                UpdateImGuiBuffers(vp.DrawData);

                CommandBuffer commandBuffer = graphicsDevice.AcquireCommandBuffer();
                Texture swapchainTexture = commandBuffer.AcquireSwapchainTexture(window);

                if (swapchainTexture != null)
                {
                    RenderCommandLists(commandBuffer, swapchainTexture, vp.DrawData);
                    graphicsDevice.Submit(commandBuffer);
                    graphicsDevice.Wait();
                }
            }
        }
        else
        {
            ImGui.Render();

            if (!mainWindow.Claimed)
            {
                return;
            }

            ImDrawDataPtr drawDataPtr = ImGui.GetDrawData();
            UpdateImGuiBuffers(drawDataPtr);

            CommandBuffer commandBuffer = graphicsDevice.AcquireCommandBuffer();
            Texture swapchainTexture = commandBuffer.AcquireSwapchainTexture(mainWindow);

            if (swapchainTexture != null)
            {
                RenderCommandLists(commandBuffer, swapchainTexture, drawDataPtr);
                graphicsDevice.Submit(commandBuffer);
                graphicsDevice.Wait();
            }
        }
    }

    private unsafe void UpdateImGuiBuffers(ImDrawDataPtr drawDataPtr)
    {
        if (drawDataPtr.TotalVtxCount == 0 || drawDataPtr.CmdListsCount == 0)
        {
            return;
        }

        CommandBuffer commandBuffer = graphicsDevice.AcquireCommandBuffer();

        if (drawDataPtr.TotalVtxCount > vertexCount)
        {
            imGuiVertexBuffer?.Dispose();

            vertexCount = (uint)(drawDataPtr.TotalVtxCount * 1.5f);
            imGuiVertexBuffer = MoonWorks.Graphics.Buffer.Create<Position2DTextureColorVertex>(
                graphicsDevice,
                BufferUsageFlags.Vertex,
                vertexCount
            );
        }

        if (drawDataPtr.TotalIdxCount > indexCount)
        {
            imGuiIndexBuffer?.Dispose();

            indexCount = (uint)(drawDataPtr.TotalIdxCount * 1.5f);
            imGuiIndexBuffer = MoonWorks.Graphics.Buffer.Create<ushort>(
                graphicsDevice,
                BufferUsageFlags.Index,
                indexCount
            );
        }

        uint vertexOffset = 0;
        uint indexOffset = 0;

        for (int n = 0; n < drawDataPtr.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawDataPtr.CmdLists[n];

            commandBuffer.SetBufferData<Position2DTextureColorVertex>(
                imGuiVertexBuffer,
                cmdList.VtxBuffer.Data,
                vertexOffset,
                (uint)cmdList.VtxBuffer.Size
            );

            commandBuffer.SetBufferData<ushort>(
                imGuiIndexBuffer,
                cmdList.IdxBuffer.Data,
                indexOffset,
                (uint)cmdList.IdxBuffer.Size
            );

            vertexOffset += (uint)cmdList.VtxBuffer.Size;
            indexOffset += (uint)cmdList.IdxBuffer.Size;
        }

        graphicsDevice.Submit(commandBuffer);
    }

    private void RenderCommandLists(CommandBuffer commandBuffer, Texture renderTexture, ImDrawDataPtr drawDataPtr)
    {
        Vector2 pos = drawDataPtr.DisplayPos;

        commandBuffer.BeginRenderPass(
            new ColorAttachmentInfo(renderTexture, clearColor)
        );

        commandBuffer.BindGraphicsPipeline(imGuiPipeline);

        // It is possible that the buffers are null (for example nothing is in our main windows viewport, then we exixt early but still clear it).
        if (imGuiVertexBuffer == null || imGuiIndexBuffer == null)
        {
            commandBuffer.EndRenderPass();
            return;
        }

        Matrix4x4 projectionMatrix = Matrix4x4.CreateOrthographicOffCenter(
            pos.X,
            pos.X + drawDataPtr.DisplaySize.X,
            pos.Y,
            pos.Y + drawDataPtr.DisplaySize.Y,
            -1.0f,
            1.0f
        );
        uint vertexUniformOffset = commandBuffer.PushVertexShaderUniforms(projectionMatrix);

        commandBuffer.BindVertexBuffers(imGuiVertexBuffer);
        commandBuffer.BindIndexBuffer(imGuiIndexBuffer, IndexElementSize.Sixteen);

        uint vertexOffset = 0;
        uint indexOffset = 0;

        for (int n = 0; n < drawDataPtr.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawDataPtr.CmdLists[n];

            for (int cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[cmdIndex];

                Texture texture = textureStorage.GetTexture(drawCmd.TextureId);

                if (texture == null)
                {
                    Logger.LogError("Texture or drawCmd.TextureId became null. Fit it!");
                    continue;
                }

                TextureSamplerBinding binding = new TextureSamplerBinding(texture, imGuiSampler);
                commandBuffer.BindFragmentSamplers(binding);

                float width = drawCmd.ClipRect.Z - (int)drawCmd.ClipRect.X;
                float height = drawCmd.ClipRect.W - (int)drawCmd.ClipRect.Y;

                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                commandBuffer.SetScissor(
                    new Rect(
                        (int)drawCmd.ClipRect.X - (int)pos.X,
                        (int)drawCmd.ClipRect.Y - (int)pos.Y,
                        (int)drawCmd.ClipRect.Z - (int)drawCmd.ClipRect.X,
                        (int)drawCmd.ClipRect.W - (int)drawCmd.ClipRect.Y
                    )
                );

                commandBuffer.DrawIndexedPrimitives(
                    vertexOffset,
                    indexOffset,
                    drawCmd.ElemCount / 3,
                    vertexUniformOffset,
                    0
                );

                indexOffset += drawCmd.ElemCount;
            }

            vertexOffset += (uint)cmdList.VtxBuffer.Size;
        }

        commandBuffer.EndRenderPass();
    }

    private void BuildFontAtlas()
    {
        CommandBuffer commandBuffer = graphicsDevice.AcquireCommandBuffer();

        ImGuiIOPtr io = ImGui.GetIO();

        io.Fonts.GetTexDataAsRGBA32(
            out nint pixelData,
            out int width,
            out int height,
            out int bytesPerPixel
        );

        Texture fontTexture = Texture.CreateTexture2D(
            graphicsDevice,
            (uint)width,
            (uint)height,
            TextureFormat.R8G8B8A8,
            TextureUsageFlags.Sampler
        );

        commandBuffer.SetTextureData(fontTexture, pixelData, (uint)(width * height * bytesPerPixel));

        graphicsDevice.Submit(commandBuffer);

        io.Fonts.SetTexID(fontTexture.Handle);
        io.Fonts.ClearTexData();

        textureStorage.Add(fontTexture);    // <-- The fontTexture seems to get lost after some time (CG?).
        this.fontTexture = fontTexture;     // <-- So we also keep a reference to make sure it doesn't happen.
    }

    #region Window
    private void CreateWindow(ImGuiViewportPtr vp)
    {
        WindowCreateInfo info = new WindowCreateInfo("Window Title", (uint)vp.Pos.X, (uint)vp.Pos.Y, ScreenMode.Windowed, PresentMode.FIFO);

        SDL2.SDL.SDL_WindowFlags flags = graphicsDevice.WindowFlags;
        flags |= SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN;

        if ((vp.Flags & ImGuiViewportFlags.NoTaskBarIcon) != 0)
        {
            flags |= SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_SKIP_TASKBAR;
        }

        if ((vp.Flags & ImGuiViewportFlags.NoDecoration) != 0)
        {
            flags |= SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_BORDERLESS;
        }
        else
        {
            flags |= SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        }

        if ((vp.Flags & ImGuiViewportFlags.TopMost) != 0)
        {
            flags |= SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_ALWAYS_ON_TOP;
        }

        Window window = new Window(info, flags);
        graphicsDevice.ClaimWindow(window, PresentMode.FIFO);

        GCHandle handle = GCHandle.Alloc(window);
        vp.PlatformUserData = (IntPtr)handle;

        windows.Add(window, handle);
    }

    private void DestroyWindow(ImGuiViewportPtr vp)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        graphicsDevice.UnclaimWindow(window);

        if (windows.TryGetValue(window, out GCHandle handle))
        {
            handle.Free();
            windows.Remove(window);
        }

        graphicsDevice.Wait();
        window.Dispose();
    }

    private void ShowWindow(ImGuiViewportPtr vp)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_ShowWindow(window.Handle);
    }

    private unsafe void GetWindowPos(ImGuiViewportPtr vp, Vector2* outPos)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_GetWindowPosition(window.Handle, out int x, out int y);
        *outPos = new Vector2(x, y);
    }

    private void SetWindowPos(ImGuiViewportPtr vp, Vector2 pos)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_SetWindowPosition(window.Handle, (int)pos.X, (int)pos.Y);
    }

    private void SetWindowSize(ImGuiViewportPtr vp, Vector2 size)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_SetWindowSize(window.Handle, (int)size.X, (int)size.Y);
    }

    private unsafe void GetWindowSize(ImGuiViewportPtr vp, Vector2* outSize)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_GetWindowSize(window.Handle, out int w, out int h);
        *outSize = new Vector2(w, h);
    }

    private void SetWindowFocus(ImGuiViewportPtr vp)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        //SDL2.SDL.SDL_SetWindowInputFocus(window.Handle);
        SDL2.SDL.SDL_RaiseWindow(window.Handle);
    }

    private byte GetWindowFocus(ImGuiViewportPtr vp)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return (byte)0;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_WindowFlags flags = (SDL2.SDL.SDL_WindowFlags)SDL2.SDL.SDL_GetWindowFlags(window.Handle);
        return (flags & SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0 ? (byte)1 : (byte)0;
    }

    private byte GetWindowMinimized(ImGuiViewportPtr vp)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return (byte)0;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        SDL2.SDL.SDL_WindowFlags flags = (SDL2.SDL.SDL_WindowFlags)SDL2.SDL.SDL_GetWindowFlags(window.Handle);
        return (flags & SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0 ? (byte)1 : (byte)0;
    }

    private unsafe void SetWindowTitle(ImGuiViewportPtr vp, IntPtr title)
    {
        if (vp.PlatformUserData == IntPtr.Zero) return;

        Window window = (Window)GCHandle.FromIntPtr(vp.PlatformUserData).Target;
        byte* titlePtr = (byte*)title;
        int count = 0;
        while (titlePtr[count] != 0)
        {
            count += 1;
        }
        SDL2.SDL.SDL_SetWindowTitle(window.Handle, System.Text.Encoding.ASCII.GetString(titlePtr, count));
    }
    #endregion

    public void Dispose()
    {
        fontTexture.Dispose();
        imGuiVertexBuffer.Dispose();
        imGuiIndexBuffer.Dispose();
        imGuiFragmentShader.Dispose();
        imGuiVertexShader.Dispose();
        imGuiPipeline.Dispose();
        imGuiSampler.Dispose();

        foreach (KeyValuePair<Window, GCHandle> window in windows)
        {
            graphicsDevice.UnclaimWindow(window.Key);
            window.Key.Dispose();
            window.Value.Free();
        }
        windows.Clear();
    }
}