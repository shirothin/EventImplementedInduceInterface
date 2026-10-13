namespace Eiii;

/// <summary>
/// 要求の値と、配送で確定した受理結果（原型 EiiiEventArgs&lt;T&gt;）。
/// <see cref="EiiiLauncher{T}"/> が接続先へ投入した後に作られ、<see cref="ILauncher{T}.Requested"/> の購読者へ渡される。
/// </summary>
/// <typeparam name="T">要求の型。</typeparam>
/// <param name="value">要求の値。</param>
/// <param name="accepted">接続先が受理したか。</param>
public class EiiiEventArgs<T>(T value, bool accepted) : EventArgs
{
    /// <summary>要求の値。</summary>
    public T Value { get; } = value;

    /// <summary>接続先が受理したか（読み取り専用。購読者は書き換えられない）。</summary>
    public bool Accepted { get; } = accepted;
}
