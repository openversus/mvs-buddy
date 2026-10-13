using RmlUi;
using RmlUi.Desktop;

namespace MvsBuddy.App;

/// <summary>
/// Clicks buttons by their labels, in order, each once the window has been quiet for a moment, through RmlUi's own input
/// path (mouse move, press, release at the button's middle); after the last has settled, hands over to
/// <paramref name="whenDone"/> (a screenshot) or closes the window. For checking the screens without a person:
/// <c>--click="Turn OpenVersus off;Go ahead" --screenshot=after.ppm</c>.
/// </summary>
internal sealed class Autopilot(RmlWindow window, UiThread ui, IEnumerable<string> labels, Action? whenDone)
{
    private readonly Queue<string> labels = new(labels);
    private int quietFrames;

    public void AfterFrame()
    {
        ui.KeepAwake = true;
        quietFrames = ui.Busy ? 0 : quietFrames + 1;
        if (quietFrames < 20)
        {
            return;
        }
        if (labels.Count == 0)
        {
            if (whenDone != null)
            {
                whenDone();
            }
            else
            {
                window.RequestExit();
            }
            return;
        }
        quietFrames = 0;
        string label = labels.Dequeue();
        var button = window.Context.GetDocument(0).QuerySelectorAll("button").FirstOrDefault(b => b.GetInnerRML().Trim() == label);
        if (button.IsNull)
        {
            Console.Error.WriteLine($"autopilot: no button \"{label}\"");
            window.RequestExit();
            return;
        }
        var at = button.GetAbsoluteOffset(BoxArea.Border);
        int x = (int)(at.X + button.GetOffsetWidth() / 2), y = (int)(at.Y + button.GetOffsetHeight() / 2);
        window.Context.ProcessMouseMove(x, y, 0);
        window.Context.ProcessMouseButtonDown(0, 0);
        window.Context.ProcessMouseButtonUp(0, 0);
        Console.WriteLine($"autopilot: clicked \"{label}\"");
    }
}
