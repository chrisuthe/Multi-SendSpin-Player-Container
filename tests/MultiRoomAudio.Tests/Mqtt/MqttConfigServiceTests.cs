using MultiRoomAudio.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttSettingsTests
{
    [Fact]
    public void Defaults_AreSafeAndDisabled()
    {
        var s = new MqttSettings();

        Assert.False(s.Enabled);
        Assert.Equal(1883, s.Port);
        Assert.False(s.UseTls);
        Assert.Equal("homeassistant", s.DiscoveryPrefix);
        Assert.Equal("multiroom-audio", s.BaseTopic);
        Assert.Null(s.Host);
    }
}

public class MqttConfigOverrideTests
{
    private static MqttSettings Apply(Dictionary<string, string?> env) =>
        MultiRoomAudio.Services.MqttConfigService.ApplyOverrides(
            new MqttSettings { Host = "yaml-host", Port = 1883 },
            env,
            _ => null, _ => null, _ => null);

    [Fact]
    public void EnvVar_OverridesYamlHost()
    {
        var result = Apply(new() { ["MQTT_HOST"] = "env-host" });
        Assert.Equal("env-host", result.Host);
    }

    [Fact]
    public void EnvEnabled_ParsesTruthyValues()
    {
        var result = Apply(new() { ["MQTT_ENABLED"] = "true" });
        Assert.True(result.Enabled);
    }

    [Fact]
    public void NoOverrides_KeepsYamlValues()
    {
        var result = Apply(new());
        Assert.Equal("yaml-host", result.Host);
        Assert.Equal(1883, result.Port);
    }
}
