using System.Threading.Channels;

namespace Eiii;

/// <summary>
/// 呼び出し元の代理として、単一の接続先（通常は Worker の入力口）へ要求を投入し、その結果をイベントで通知する（原型 EiiiLauncher&lt;T&gt;）。
/// </summary>
/// <remarks>
/// <para>接続: 同時に接続できるのは 1 つだけ。<see cref="Attach"/> の戻り値を Dispose すると外れ、その後は別の接続先へ接続し直せる。
/// 接続を保持するのは Launcher 自身で、戻り値を保持しなくても、GC によって接続が外れることはない。</para>
/// <para>解除の範囲: Dispose で外れるのは Launcher が持つ接続先への参照だけで、Worker の停止、入力の Close（<c>TryComplete</c>）は行わない。
/// Launcher は Worker の生存を保証しない。</para>
/// <para>Dispose との競合: 接続先は <see cref="Request(object, T)"/> がそれを読み取った時点で決まる。Dispose が戻った後に始まった要求は配送しない。
/// それより前に始まった要求の書き込みが、Dispose が戻った後に起きないことまでは保証しない（Dispose は待たない）。</para>
/// </remarks>
/// <typeparam name="T">要求の型。</typeparam>
public class EiiiLauncher<T> : ILauncher<T>
{
    private Attachment? current;   // 設定と解除は Interlocked.CompareExchange、読み取りは Volatile.Read

    /// <inheritdoc/>
    public event EventHandler<object, EiiiEventArgs<T>>? Requested;

    /// <inheritdoc/>
    public bool IsAttached => Volatile.Read(ref current) is not null;

    /// <summary>接続先（通常は Worker の <c>Input</c>）へ接続する。</summary>
    /// <param name="target">要求の投入先。</param>
    /// <returns>Dispose すると、この接続を外す。二度目以降の Dispose と、すでに別の接続に替わった後の Dispose は何もしない。</returns>
    /// <exception cref="InvalidOperationException">すでに接続している。接続し直すには、先に前の戻り値を Dispose する。</exception>
    public IDisposable Attach(ChannelWriter<T> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var attachment = new Attachment(this, target);
        if (Interlocked.CompareExchange(ref current, attachment, null) is not null)
            throw new InvalidOperationException("この Launcher はすでに接続している。接続し直すには、先に Attach の戻り値を Dispose する。");
        return attachment;
    }

    /// <inheritdoc/>
    public bool Request(T value) => Request(this, value);

    /// <inheritdoc/>
    /// <remarks>
    /// 順序: 接続先を一度だけ読み取り → TryWrite で受理を確定 → <see cref="Requested"/> を発火 → 結果を返す。
    /// 購読者が例外を投げると、その例外は呼び出し元へそのまま伝わる。その場合でも、要求はすでに受理されていることがある。
    /// 例外を投げた購読者より後の購読者が呼ばれないことがあるのは、通常のマルチキャストのイベントと同じ。
    /// </remarks>
    public bool Request(object sender, T value)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var accepted = Volatile.Read(ref current)?.Target.TryWrite(value) ?? false;
        OnRequested(sender, new EiiiEventArgs<T>(value, accepted));
        return accepted;
    }

    /// <summary>
    /// <see cref="Requested"/> を発行する（原型の public OnChanged に相当。sender を呼び出し元に改め、protected virtual にした）。
    /// 投入の結果が確定した後に、購読者は呼び出し元のスレッドで同期的に呼ばれる。
    /// </summary>
    /// <param name="sender">要求の呼び出し元。</param>
    /// <param name="e">要求の値と、確定した受理結果。</param>
    protected virtual void OnRequested(object sender, EiiiEventArgs<T> e)
    {
        EventHandler<object, EiiiEventArgs<T>>? handler = Requested;   // 読み取り後に購読解除されても安全なよう、1 回だけ読む
        if (handler is not null) handler(sender, e);                 // マルチキャスト: 登録順にすべての購読者を呼ぶ
    }

    /// <summary>現在の接続が <paramref name="attachment"/> のときだけ外す（古い接続の Dispose で新しい接続を外さない）。</summary>
    private void Detach(Attachment attachment) => Interlocked.CompareExchange(ref current, null, attachment);

    /// <summary>1 回の接続。ファイナライザーは持たない（GC で接続が外れることはない）。</summary>
    private sealed class Attachment(EiiiLauncher<T> owner, ChannelWriter<T> target) : IDisposable
    {
        public ChannelWriter<T> Target { get; } = target;

        public void Dispose() => owner.Detach(this);
    }
}
