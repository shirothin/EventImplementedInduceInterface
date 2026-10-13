namespace Eiii;

/// <summary>
/// <c>ProcessAsync</c> に渡した要求 1 件の終わり方（<c>EiiiWorker&lt;TIn, TOut&gt;.OnRequestEnded</c> で通知する）。
/// Worker の中での行方だけを表し、業務上の成否は表さない。
/// </summary>
public enum RequestEnd
{
    /// <summary><c>ProcessAsync</c> が正常に return した（停止の後に return した場合も含む）。処理が業務上成功したことは表さない。</summary>
    Processed,

    /// <summary><c>ProcessAsync</c> が例外を投げた（停止以外のキャンセル例外と、callback の例外を含む）。Worker は Faulted になる。</summary>
    Failed,

    /// <summary>停止が要求された状態で <c>ProcessAsync</c> がキャンセル例外を投げた。Worker は Canceled になる。</summary>
    Stopped,
}
