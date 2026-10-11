namespace EventImplementedInduceInterface;

/// <summary>
/// ワーカーの出力を観測するコールバック（原型 TaskToCallback(string) の出力型を任意化）。
/// ワーカーのスレッドで同期的に呼ばれ、流量制御には関与しない。例外を投げるとワーカーは Faulted になる。
/// コールバックは、出力が下流の Channel に受理されたこと（下流が未接続なら出力が生成されたこと）を示す。下流での処理の完了は保証しない。
/// </summary>
/// <typeparam name="TOut">出力の型。</typeparam>
/// <param name="value">出力された値。</param>
public delegate void TaskToCallback<TOut>(TOut value);
