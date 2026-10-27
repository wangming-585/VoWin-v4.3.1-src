using Xunit;
using VoSharp.Sim;
using VoSharp.Sip;

namespace VoWin.Tests;

public class DeviceDisguiseSipTests
{
    private static ImsProfile Profile(DeviceProfile? dp)
    {
        return new ImsProfile(
            "0imsi@nai.epc.mnc489.mcc234.3gppnetwork.org",
            "sip:+441234567890@epdg.epc.mnc489.mcc234.pub.3gppnetwork.org",
            "epdg.epc.mnc489.mcc234.pub.3gppnetwork.org",
            "354996261234567",
            "127.0.0.1",
            PAccessNetworkInfo: "IEEE-802.11;i-wlan-node-id=0002c9a3b4c5",
            DeviceProfile: dp);
    }

    [Fact]
    public void PanI_WithDeviceProfile_AppendsLocation()
    {
        var pani = ImsRegisterBuilder.BuildAccessNetworkInfo("0imsi@nai.epc.mnc489.mcc234.3gppnetwork.org", DeviceProfiles.IPhone18ProUk());
        Assert.Contains(";i-wlan-location=51.5074;-0.1278", pani);
    }

    [Fact]
    public void PanI_WithoutProfile_HasNoLocation()
    {
        var pani = ImsRegisterBuilder.BuildAccessNetworkInfo("0imsi@nai.epc.mnc489.mcc234.3gppnetwork.org", null);
        Assert.DoesNotContain("i-wlan-location", pani);
    }

    [Fact]
    public void LondonDateHeader_UsesLondonTimeFormat()
    {
        var date = ImsRegisterBuilder.BuildLondonDateHeader("Europe/London");
        Assert.Matches(@"^\w{3}, \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} GMT$", date);
    }

    [Fact]
    public void InitialRegister_HasDisguiseHeaders()
    {
        var msg = ImsRegisterBuilder.BuildInitialRegister(Profile(DeviceProfiles.IPhone18ProUk()), "call-1", 1);
        Assert.Equal("iPhone Pro/18", msg.Headers["User-Agent"][0]);
        Assert.Contains("Geolocation", msg.Headers.Keys);
        Assert.Contains("cid:vlw0001@ts.3gppnetwork.org", msg.Headers["Geolocation"][0]);
        Assert.Equal("application/pidf+xml", msg.Headers["Content-Type"][0]);
        Assert.Contains("gml:pos", msg.Body);
        Assert.Contains("51.5074", msg.Body);
        Assert.Contains("GMT", msg.Headers["Date"][0]);
    }

    [Fact]
    public void InitialRegister_WithoutProfile_UsesLegacyAgent()
    {
        var msg = ImsRegisterBuilder.BuildInitialRegister(Profile(null), "call-2", 1);
        Assert.Equal("voSharp/1.0.0 (Windows; C# Telephony Kernel)", msg.Headers["User-Agent"][0]);
        Assert.DoesNotContain("Geolocation", msg.Headers.Keys);
    }
}
