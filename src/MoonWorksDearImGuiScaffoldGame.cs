using ImGuiNET;
using MoonWorks;
using MoonWorks.Graphics;

namespace MoonWorksDearImGuiScaffold;

class MoonWorksDearImGuiScaffoldGame : Game
{
    private readonly ImGuiController imGuiController;

    public MoonWorksDearImGuiScaffoldGame(
        WindowCreateInfo windowCreateInfo,
        FrameLimiterSettings frameLimiterSettings,
        bool debugMode
    ) : base(windowCreateInfo, frameLimiterSettings, 60, debugMode)
    {
        // Render the mouse in software with ImGui.
        Inputs.Mouse.Hidden = true;
        imGuiController = new ImGuiController(GraphicsDevice, MainWindow, Inputs, Color.CornflowerBlue);
        imGuiController.OnGui += ImGui.ShowDemoWindow;
    }

    protected override void Update(System.TimeSpan dt)
    {
        imGuiController.Update((float)dt.TotalSeconds);
    }

    protected override void Draw(double alpha)
    {
        imGuiController.Render();
    }

    protected override void Destroy()
    {
        imGuiController?.Dispose();
    }
}