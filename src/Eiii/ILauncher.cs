namespace EventImplementedInduceInterface;

/// <summary>
/// 原型の独自デリゲート（原型 ILauncher.cs:3）。比較学習のため、標準の <see cref="EventHandler{TEventArgs}"/> ではなくこれを使う。
/// 型引数の数が異なるため、<c>System.EventHandler&lt;TEventArgs&gt;</c> と名前解決で衝突しない。
/// </summary>
/// <typeparam name="TSender">送信元の型。</typeparam>
/// <typeparam name="TEventArgs">イベント情報の型。</typeparam>
/// <param name="sender">イベントの送信元。</param>
/// <param name="e">イベント情報。</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "原型の独自デリゲートを比較学習のため維持する（命名変更は Phase 2 完了後に判断）。")]
public delegate void EventHandler<TSender, TEventArgs>(TSender sender, TEventArgs e);

/// <summary>型付き要求を発行する入口（原型 ILauncher&lt;T&gt;）。</summary>
/// <remarks>制約: Launcher と Worker は 1 対 1 で使う。1 つの Launcher を複数の Worker が購読した場合の Accepted の競合（最後の書き込みが残る）は、初期版ではサポートしない。</remarks>
/// <typeparam name="T">要求の型。</typeparam>
public interface ILauncher<T>
{
    /// <summary>要求が発行されたことの通知。sender は要求の呼び出し元。</summary>
    event EventHandler<object, EiiiEventArgs<T>>? Raise;

    /// <summary>要求を発行する（原型の Trigger プロパティに相当）。</summary>
    /// <param name="sender">要求の呼び出し元。</param>
    /// <param name="value">要求の値。</param>
    /// <returns>購読者が受理したら true。購読者がいない、または満杯・入力完了で受理されなければ false。</returns>
    bool Trigger(object sender, T value);
}
