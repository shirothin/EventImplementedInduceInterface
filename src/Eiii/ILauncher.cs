namespace Eiii;

/// <summary>
/// 原型の独自デリゲート（原型 ILauncher.cs:3）。比較学習のため、標準の <see cref="EventHandler{TEventArgs}"/> ではなくこれを使う。
/// 型引数の数が異なるため、<c>System.EventHandler&lt;TEventArgs&gt;</c> と名前解決で衝突しない。
/// </summary>
/// <typeparam name="TSender">送信元の型。</typeparam>
/// <typeparam name="TEventArgs">イベント情報の型。</typeparam>
/// <param name="sender">イベントの送信元。</param>
/// <param name="e">イベント情報。</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "原型の独自デリゲートを比較学習のため維持する（Phase 2 完了時の命名変更でも維持すると決定）。")]
public delegate void EventHandler<TSender, TEventArgs>(TSender sender, TEventArgs e);

/// <summary>型付き要求を発行する入口（原型 ILauncher&lt;T&gt;）。要求する側に渡す。</summary>
/// <remarks>接続を張り替える操作（<see cref="EiiiLauncher{T}.Attach"/>）はこのインターフェイスに含めない。要求する側は接続先を知らなくてよい。</remarks>
/// <typeparam name="T">要求の型。</typeparam>
public interface ILauncher<T>
{
    /// <summary>
    /// 要求を投入した後の通知（観測用）。sender は要求の呼び出し元。<see cref="EiiiEventArgs{T}.Accepted"/> は投入で確定した結果。
    /// 購読者は <see cref="Request(object, T)"/> を呼んだスレッドで同期的に呼ばれる。
    /// </summary>
    event EventHandler<object, EiiiEventArgs<T>>? Requested;

    /// <summary>接続先があるとき true。読んだ直後に変わり得る（瞬間値）。</summary>
    bool IsAttached { get; }

    /// <summary>要求を投入する。sender は Launcher 自身になる（原型の Raise(this, e) と同じ）。</summary>
    /// <param name="value">要求の値。</param>
    /// <returns>接続先が受理したら true。</returns>
    bool Request(T value);

    /// <summary>要求を投入する（原型の Trigger プロパティに相当）。</summary>
    /// <param name="sender">要求の呼び出し元。</param>
    /// <param name="value">要求の値。</param>
    /// <returns>接続先が受理したら true。接続先がない、または満杯・入力完了で受理されなければ false。</returns>
    bool Request(object sender, T value);
}
