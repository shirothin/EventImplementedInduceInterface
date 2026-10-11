namespace Eiii;

/// <summary>
/// 発行された要求の値と、受理結果の書き戻し欄（原型 EiiiEventArgs&lt;T&gt;）。
/// 受理結果を購読者が書き戻す形は、<see cref="System.ComponentModel.CancelEventArgs.Cancel"/> と同じ慣習。
/// </summary>
/// <typeparam name="T">要求の型。</typeparam>
/// <param name="value">要求の値。</param>
public class EiiiEventArgs<T>(T value) : EventArgs
{
    /// <summary>要求の値。</summary>
    public T Value { get; } = value;

    /// <summary>購読者が要求を受理したら true を書き込む。</summary>
    /// <remarks>制約: Launcher と Worker は 1 対 1 で使う。1 つの Launcher を複数の Worker が購読した場合の Accepted の競合（最後の書き込みが残る）は、初期版ではサポートしない。</remarks>
    public bool Accepted { get; set; }
}
