using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class RetainedPublishCacheTests
{
    private const string Topic = "multiroom-audio/player/sendspin-kitchen-33fa00a6/state";
    private const string Payload = """{"state":"connected","delay_ms":0}""";

    [Fact]
    public void FirstRetainedPublish_GoesOnTheWire()
    {
        var cache = new RetainedPublishCache();

        Assert.True(cache.ShouldPublish(Topic, Payload, retain: true));
    }

    [Fact]
    public void IdenticalRetainedPayload_IsSuppressed()
    {
        var cache = new RetainedPublishCache();
        cache.ShouldPublish(Topic, Payload, retain: true);
        cache.Record(Topic, Payload, retain: true);

        Assert.False(cache.ShouldPublish(Topic, Payload, retain: true));
    }

    [Fact]
    public void ChangedRetainedPayload_GoesOnTheWire()
    {
        var cache = new RetainedPublishCache();
        cache.Record(Topic, Payload, retain: true);

        Assert.True(cache.ShouldPublish(Topic, """{"state":"buffering","delay_ms":0}""", retain: true));
    }

    [Fact]
    public void UnrecordedPublish_IsRetried()
    {
        // A publish that threw is never recorded, so the value must still be sent next time
        // rather than being assumed delivered.
        var cache = new RetainedPublishCache();
        cache.ShouldPublish(Topic, Payload, retain: true);

        Assert.True(cache.ShouldPublish(Topic, Payload, retain: true));
    }

    [Fact]
    public void NonRetainedPayload_IsNeverSuppressed()
    {
        var cache = new RetainedPublishCache();
        cache.Record(Topic, Payload, retain: false);

        Assert.True(cache.ShouldPublish(Topic, Payload, retain: false));
    }

    [Fact]
    public void Clear_ReprimesEveryTopic()
    {
        // Models a broker restart: the retained set may be gone, so the next announce has to
        // publish everything again.
        var cache = new RetainedPublishCache();
        cache.Record(Topic, Payload, retain: true);
        Assert.False(cache.ShouldPublish(Topic, Payload, retain: true));

        cache.Clear();

        Assert.True(cache.ShouldPublish(Topic, Payload, retain: true));
    }

    [Fact]
    public void TopicsAreTrackedIndependently()
    {
        var cache = new RetainedPublishCache();
        cache.Record(Topic, Payload, retain: true);

        Assert.True(cache.ShouldPublish("multiroom-audio/amp/virtual_da1a7d94_1/state", Payload, retain: true));
    }

    [Fact]
    public void UnchangedFanOut_CollapsesToOnlyTheChangedTopic()
    {
        // The #256 scenario: one player changes state, but the bridge republishes every
        // player and amp topic because the change event doesn't say what changed.
        var cache = new RetainedPublishCache();
        var topics = Enumerable.Range(0, 40).Select(i => $"multiroom-audio/entity/{i}/state").ToArray();

        foreach (var t in topics)
        {
            cache.ShouldPublish(t, "unchanged", retain: true);
            cache.Record(t, "unchanged", retain: true);
        }

        var wouldPublish = topics.Count(t => cache.ShouldPublish(t, t == topics[7] ? "changed" : "unchanged", retain: true));

        Assert.Equal(1, wouldPublish);
    }
}
