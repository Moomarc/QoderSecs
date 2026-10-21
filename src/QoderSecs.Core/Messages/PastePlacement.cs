namespace QoderSecs.Core.Messages;

/// <summary>结构树上的一个落点能不能收粘贴进来的克隆，以什么方式收。</summary>
public enum PastePlacement
{
    /// <summary>这个位置不收：既不能当它的子项，也不能当它的同级。</summary>
    Nowhere,

    /// <summary>成为它的子项。空的 &lt;Structure&gt; 走这一档，插进去的就是根。</summary>
    AsChild,

    /// <summary>成为它的同级，紧跟其后。根元素没有同级，走不到这一档。</summary>
    AsSibling,

    /// <summary>成为它的同级，紧挨在它前面。拖动落在目标行上段时走这一档。</summary>
    AsSiblingBefore
}

/// <summary>
/// 拖到一个落点上，原来那个节点是被搬走，还是留在原地、落点上多一份副本。
/// 收不收由 <see cref="PastePlacement"/> 判，这一条只判收下之后怎么办。
/// </summary>
public enum DropWay
{
    /// <summary>这个落点不收。</summary>
    Nowhere,

    /// <summary>把原节点搬过去，原来的位置少一个。</summary>
    Move,

    /// <summary>原节点不动，在落点插一份克隆。</summary>
    Copy
}
