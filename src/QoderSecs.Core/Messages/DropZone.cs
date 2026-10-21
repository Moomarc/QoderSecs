namespace QoderSecs.Core.Messages;

/// <summary>
/// 拖动时鼠标落在目标行的哪一段。上段插到它前面，中段放进它里面，下段插到它后面。
/// 中段的含义和原来的整行落点一样：能收子项就当子项，收不了才退到同级。
/// </summary>
public enum DropZone
{
    Top,
    Middle,
    Bottom
}
