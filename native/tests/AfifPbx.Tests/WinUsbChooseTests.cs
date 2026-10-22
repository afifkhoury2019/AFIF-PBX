using AfifPbx.Gsm;
using Xunit;

namespace AfifPbx.Tests;

public class WinUsbChooseTests
{
    private static WinUsbInterface I(int mi, bool interrupt = false, int cls = 0xFF, string pid = "1506") =>
        new($"path{mi}", "12D1", pid, mi, cls, 2, mi, true, true, interrupt);

    [Fact]
    public void Classic_three_port_stick_picks_pcui_and_voice()
    {
        // MI0 modem (answers AT), MI1 voice, MI2 PC UI (answers AT)
        var itfs = new[] { I(0, interrupt: true), I(1), I(2) };
        var (at, audio) = WinUsb.Choose(itfs, i => i.Mi is 0 or 2);
        Assert.Equal(2, at!.Mi);
        Assert.Equal(1, audio!.Mi);
    }

    [Fact]
    public void Network_interface_is_never_taken_for_voice()
    {
        // MI0 modem, MI1 network (interrupt + bulk), MI2 voice, MI3 PC UI
        var itfs = new[] { I(0, interrupt: true), I(1, interrupt: true), I(2), I(3) };
        var (at, audio) = WinUsb.Choose(itfs, i => i.Mi is 0 or 3);
        Assert.Equal(3, at!.Mi);
        Assert.Equal(2, audio!.Mi);
    }

    [Fact]
    public void Settings_override_detection()
    {
        var itfs = new[] { I(0), I(1), I(2) };
        var (at, audio) = WinUsb.Choose(itfs, _ => throw new InvalidOperationException("should not probe"), "USB:MI_00", "USB:MI_02");
        Assert.Equal(0, at!.Mi);
        Assert.Equal(2, audio!.Mi);
    }

    [Fact]
    public void Nothing_answering_means_no_choice()
    {
        var (at, _) = WinUsb.Choose(new[] { I(0), I(1) }, _ => false);
        Assert.Null(at);
    }
}
