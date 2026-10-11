namespace Eiii;

/// <summary>
/// 呼び出し元の代理として要求のイベントを発行する（原型 EiiiLauncher&lt;T&gt;）。
/// C# ではフィールドのように宣言したイベントは宣言した型の内側からしか呼び出せないため、外部からの要求をこの型が中継する。
/// </summary>
/// <typeparam name="T">要求の型。</typeparam>
public class EiiiLauncher<T> : ILauncher<T>
{
    /// <inheritdoc/>
    public event EventHandler<object, EiiiEventArgs<T>>? Requested;

    /// <inheritdoc/>
    public bool Request(object sender, T value)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var e = new EiiiEventArgs<T>(value);
        OnRequested(sender, e);
        return e.Accepted;
    }

    /// <summary>
    /// <see cref="Requested"/> を発行する（原型の public OnChanged に相当。sender を呼び出し元に改め、protected virtual にした）。
    /// 購読者は呼び出し元のスレッドで同期的に呼ばれる。
    /// </summary>
    /// <param name="sender">要求の呼び出し元。</param>
    /// <param name="e">要求の値と受理結果。</param>
    protected virtual void OnRequested(object sender, EiiiEventArgs<T> e)
    {
        EventHandler<object, EiiiEventArgs<T>>? handler = Requested;   // 読み取り後に購読解除されても安全なよう、1 回だけ読む
        if (handler is not null) handler(sender, e);                 // マルチキャスト: 登録順にすべての購読者を呼ぶ
    }
}
