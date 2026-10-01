using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>
/// 常量等价测试：各域集合名常量值逐一等于拆分前 MongoCollectionNames 的值（防「连错库」）。
/// </summary>
public class CollectionNamesEquivalenceTests
{
    [Fact]
    public void CoreCollectionNames_ValuesAreStable()
    {
        Assert.Equal("zvtuber_meta", CoreCollectionNames.Meta);
    }

    [Fact]
    public void CookieCollectionNames_ValuesAreStable()
    {
        Assert.Equal("bilibili_cookies", CookieCollectionNames.Cookies);
        Assert.Equal("bilibili_cookie_sessions", CookieCollectionNames.CookieSessions);
        Assert.Equal("bilibili_cookie_account_info", CookieCollectionNames.CookieAccountInfo);
        Assert.Equal("weibo_cookies", CookieCollectionNames.WeiboCookies);
        Assert.Equal("weibo_cookie_sessions", CookieCollectionNames.WeiboCookieSessions);
    }

    [Fact]
    public void LiveCollectionNames_ValuesAreStable()
    {
        Assert.Equal("bilibili_subscriptions", LiveCollectionNames.Subscriptions);
        Assert.Equal("bilibili_account_basic_info", LiveCollectionNames.AccountBasicInfo);
        Assert.Equal("bilibili_live_status", LiveCollectionNames.LiveStatus);
        Assert.Equal("bilibili_live_event", LiveCollectionNames.LiveEvent);
        Assert.Equal("bilibili_user_space_timeline", LiveCollectionNames.SpaceTimeline);
        Assert.Equal("bilibili_live_session", LiveCollectionNames.LiveSession);
        Assert.Equal("bilibili_live_event_text_index", LiveCollectionNames.LiveEventTextIndex);
        Assert.Equal("bilibili_live_guards", LiveCollectionNames.LiveGuards);
    }

    [Fact]
    public void WeiboCollectionNames_ValuesAreStable()
    {
        Assert.Equal("weibo_subscriptions", WeiboCollectionNames.WeiboSubscriptions);
        Assert.Equal("weibo_user_timeline", WeiboCollectionNames.WeiboUserTimeline);
    }

    [Fact]
    public void RecordingCollectionNames_ValuesAreStable()
    {
        Assert.Equal("recording_status", RecordingCollectionNames.RecordingStatus);
        Assert.Equal("recordings", RecordingCollectionNames.Recordings);
    }

    [Fact]
    public void TranscriptCollectionNames_ValuesAreStable()
    {
        Assert.Equal("transcript_tasks", TranscriptCollectionNames.TranscriptTasks);
        Assert.Equal("transcript_segments", TranscriptCollectionNames.TranscriptSegments);
        Assert.Equal("transcript_models", TranscriptCollectionNames.TranscriptModels);
        Assert.Equal("transcript_auto_enqueue", TranscriptCollectionNames.TranscriptAutoEnqueue);
    }

    [Fact]
    public void TrainingCollectionNames_ValuesAreStable()
    {
        Assert.Equal("training_datasets", TrainingCollectionNames.TrainingDatasets);
        Assert.Equal("training_dataset_items", TrainingCollectionNames.TrainingDatasetItems);
        Assert.Equal("training_providers", TrainingCollectionNames.TrainingProviders);
        Assert.Equal("training_models", TrainingCollectionNames.TrainingModels);
        Assert.Equal("training_emotion_tasks", TrainingCollectionNames.TrainingEmotionTasks);
        Assert.Equal("training_feature_points", TrainingCollectionNames.TrainingFeaturePoints);
    }

    [Fact]
    public void NotificationCollectionNames_ValuesAreStable()
    {
        Assert.Equal("notification_subscriptions", NotificationCollectionNames.NotificationSubscriptions);
    }
}
