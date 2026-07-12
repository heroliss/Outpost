using Game.Framework.Event;

namespace Game.Outpost.Net
{
    // 给 protoc 生成的推送消息补 IEvent：生成类是 partial，本 partial 让它同时是框架事件——
    // 才能经 ws.RegisterPush<NewRecordPushEvent> 映射、被 Bag.Subscribe<NewRecordPushEvent> 消费（§32）。
    // 反序列化不走 JsonUtility（那才要求 struct）而走 Google.Protobuf 的 Parser，所以 class 消息合法。
    public sealed partial class NewRecordPushEvent : IEvent
    {
    }
}
