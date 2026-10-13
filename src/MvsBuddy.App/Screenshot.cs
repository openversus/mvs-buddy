using System.Runtime.InteropServices;
using System.Text;
using RmlUi.Desktop;

namespace MvsBuddy.App;

/// <summary>
/// Saves the window as a PPM once nothing has run for a moment, then closes it: the OpenGL back buffer, read before it is
/// shown (as RmlUiFlat's demo does). Linux with the OpenGL renderer only.
/// </summary>
internal static unsafe partial class Screenshot
{
    public static Action When(RmlWindow window, UiThread ui, string path)
    {
        int quietFrames = 0;
        bool saving = false;
        return () =>
        {
            ui.KeepAwake = true;
            quietFrames = ui.Busy ? 0 : quietFrames + 1;
            if (saving || quietFrames < 20)
            {
                return;
            }
            saving = true;
            if (!OperatingSystem.IsLinux() || window.Renderer != RmlRenderer.OpenGL)
            {
                Console.Error.WriteLine("--screenshot needs Linux and the OpenGL renderer");
                window.RequestExit();
                return;
            }
            var size = window.Context.GetDimensions();
            window.AfterRender += () =>
            {
                Save(path, size.X, size.Y);
                window.RequestExit();
            };
            window.PowerSave = false;
        };
    }

    private static void Save(string path, int width, int height)
    {
        const uint GL_RGB = 0x1907, GL_UNSIGNED_BYTE = 0x1401, GL_PACK_ALIGNMENT = 0x0D05;
        var pixels = new byte[width * height * 3];
        glPixelStorei(GL_PACK_ALIGNMENT, 1);
        fixed (byte* p = pixels)
        {
            glReadPixels(0, 0, width, height, GL_RGB, GL_UNSIGNED_BYTE, p);
        }
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        for (int row = height - 1; row >= 0; row--)
        {
            file.Write(pixels, row * width * 3, width * 3);
        }
    }

    [LibraryImport("libGL.so.1")]
    private static partial void glReadPixels(int x, int y, int width, int height, uint format, uint type, void* data);

    [LibraryImport("libGL.so.1")]
    private static partial void glPixelStorei(uint pname, int param);
}
