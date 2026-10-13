# Phase 2 — Migration Notes（移行ノート）

## 第二次改良（`refactor/phase2`、2026-10-13）

> 正式なリリースではありません。バージョン番号とリリースのタグはまだありません。`refactor/phase2` ブランチの開発中の内容について、以前の版からの移行に必要な変更をまとめたものです。

Phase 2 の最初の完成版（`f481583`）から、第二次改良の最終版（`6d364c0`）までの変更です。
停止・終了・通知の契約を見直し、曖昧だった動作を決めました。**ソースの互換性とバイナリの互換性が失われる変更を含みます。**

使い方の全体は [README.md](README.md) を、各メンバーの詳しい契約は `src/Eiii` の XML ドキュメントを参照してください。

### 互換性について

- この版は、`f481583` 以前の Eiii とも、2018 年版の原型（`legacy/`、`master`）とも、互換性を保証しません。
- 下の「検証」に書いた範囲で、既存の利用アプリケーションが変更なしに動くことを確かめました。これは、その範囲で問題が見つからなかったことを示すもので、すべての利用方法での互換性を示すものではありません。

---

### ソースの互換性が失われる変更

| 変更 | 以前 | この版 | 移行の方法 |
|---|---|---|---|
| Registry の一括操作の削除 | `StartAll()`、`StopAllAsync()`、`CompleteAllAsync()` | 削除。Registry は `Add` / `Get` / `GetAll` だけ | `GetAll()`（登録順のスナップショット）や `Get` で Worker を取り出し、接続の構成に合わせた順序で 1 つずつ `Start` / `StopAsync` / `CompleteAsync` を呼ぶ（直列なら、起動は下流から、停止と完了は上流から） |
| Worker の Launcher 購読の削除 | `worker.Attach(launcher)`、`worker.Detach(launcher)`（Worker が `Requested` を購読して `Accepted` を書き込む） | 削除 | `var a = launcher.Attach(worker.Input);` で接続し、外すときは `a.Dispose()` |
| `EiiiEventArgs<T>` | `new EiiiEventArgs<T>(value)`、`Accepted { get; set; }` | `new EiiiEventArgs<T>(value, accepted)`、`Accepted { get; }`（読み取り専用） | 受理の結果は Launcher が確定してから渡す。購読者は書き換えられない |
| `ILauncher<T>` のメンバー追加 | `Requested`、`Request(object, T)` | `IsAttached`、`Request(T)` を追加 | `ILauncher<T>` を自分で実装している場合は、2 つのメンバーを実装する |
| `LinkTo` の delegate への変換 | `Action<ChannelWriter<int>> connect = worker.LinkTo;`（メソッドグループを 1 引数の delegate に変換） | 引数が 2 つになったため、コンパイルエラー（CS0123） | `Action<ChannelWriter<int>> connect = target => worker.LinkTo(target);` |

### バイナリの互換性が失われる変更

| 変更 | 以前 | この版 |
|---|---|---|
| `LinkTo` の引数 | `LinkTo(ChannelWriter<TOut> target)` | `LinkTo(ChannelWriter<TOut> target, bool propagateCompletion = true)` |

通常の直接呼び出し（`worker.LinkTo(writer)`）は、ソースを変えずにそのまま再コンパイルできます（delegate に変換している場合は、上の「ソースの互換性が失われる変更」を参照）。ただし、CLR 上のメソッドのシグネチャは変わるため、**`LinkTo` を呼んでいる既存のコンパイル済みのアセンブリ（以前の版に対してビルドしたもの）は、再ビルドが必要です**（再ビルドしないと、呼び出しの時点で `MissingMethodException` になります）。

### 動作の変更

| 変更 | 以前 | この版 |
|---|---|---|
| 停止と取り出しの競合 | `StopAsync` と競合すると、キューから取り出した要求が `ProcessAsync` に渡らずに失われることがあった | 取り出した要求は必ず `ProcessAsync` に一度だけ渡す（取り消し済みのトークンで渡ることがある）。まだ取り出していない要求はキューに残る |
| 停止以外のキャンセル例外 | `ProcessAsync` などから出たキャンセル例外は、停止の要求がなくても `Canceled` になった | その時点で停止が要求されていなければ、`InvalidOperationException` に包んで（元の例外は `InnerException`）`Faulted` にする。`Canceled` は `StopAsync` による停止だけを表す |
| 正常終了した Worker の再起動 | `RanToCompletion` の後も、`Start` / `RestartAsync` で新しい実行を始めていた（入力は閉じたままなので、すぐに終わり、`OnInputCompletedAsync` と下流への完了の伝播がもう一度行われた） | `RanToCompletion` は終端。`Start` / `RestartAsync` は何もしない（`Execution` は同じ Task のまま）。`Faulted` / `Canceled` からは従来どおり再開できる |
| `LinkTo` の回数 | 呼ぶたびに接続先を置き換えていた | Worker の生存期間を通じて一度だけ。二度目は `InvalidOperationException`（同じ接続先でも） |
| Launcher の投入 | `Requested` の購読者（Worker）が `TryWrite` し、`Accepted` を書き込んでいた | Launcher が `Attach` した単一の接続先へ自分で `TryWrite` し、受理の結果を確定してから `Requested` を発火する。`Requested` は観測専用。接続先がなければ false |

### 追加

- `EiiiWorker.CompleteAsync()`: その Worker の入力を閉じ、呼び出した時点のその Worker 自身の実行（`Execution`）の終了を待つ。
  - true は、その実行が正常終了（`RanToCompletion`）したこと。業務上の成功は表さない。
  - false は、その実行が正常終了しなかったこと（未起動、`Faulted`、`Canceled`）。例外は再スローしないので、原因は `Execution` で確かめる。false のとき、未処理の要求が残ることも、再実行されることも保証しない。
  - `LinkTo` 先の Worker の処理の完了は待たず、保証もしない（完了を伝播する設定なら、下流の入力を閉じるところまで）。パイプライン全体の完了を待つには、上流から順に各 Worker の `CompleteAsync` を呼ぶ（下流が出力を受け取れることが前提。下流が受け取らずにキューが満杯になると、上流の `EmitAsync` が待ち続け、`CompleteAsync` も戻らない。`CompleteAsync` は停止の要求ではない）。下流の `CompleteAsync` も自分の入力を閉じるので、`propagateCompletion: false`（上流の正常終了で下流の入力を閉じない）の場合も、すべての上流の `CompleteAsync` が終わった後に下流の `CompleteAsync` を呼べばよい。上流が終わる前に下流の入力を閉じると、上流の `EmitAsync` の書き込みが失敗し、上流は `Faulted` になる。
  - 待つ対象は `CompleteInput` より前に取得した実行だけ。同時に `StopAsync` / `RestartAsync` が行われても、その後に始まった実行は待たない。
- `EiiiWorker<TIn, TOut>.OnRequestEnded(request, ending, exception)` と `RequestEnd`（`Processed` / `Failed` / `Stopped`）: `ProcessAsync` の呼び出しごとにちょうど 1 回、要求の終わり方を通知する。`Processed` は業務上の成功を表さない。「ちょうど 1 回」は呼び出しの回数の保証で、通知を受けた側の処理が終わったことの保証ではない。フックが例外を投げると、どの終わり方（`Stopped` を含む）でも Worker は `Faulted` になる（フックが正常に戻った場合だけ、`Stopped` は `Canceled` に、`Failed` は `Faulted` に対応する）。
- `EiiiWorkerRegistry.GetAll()`: 登録順のスナップショット。
- `EiiiLauncher<T>.Attach(ChannelWriter<T>)`（戻り値の `IDisposable` で外す）、`IsAttached`、`Request(T)`。
- `LinkTo` の `propagateCompletion`: 正常終了を下流へ伝えるか（既定は true）。合流（複数の上流が 1 つの下流につながる構成）では false を指定し、下流の入力は利用者が閉じる。

### 文書にした契約（動作の変更はなし）

- Worker の生存責任: Worker を必要な期間生存させる責任は利用者にある。`Start` は生存を保証しない（外部から到達できなくなった実行中の Worker は GC に回収され得る）。`StopAsync` による停止は、直ちに回収されることを保証しない。
- `EmitAsync`: 接続先があれば書き込みが完了してから callback を呼ぶ。書き込みがキャンセルされた場合や接続先が閉じている場合、callback は呼ばない。
- 禁止事項: Worker の中から自分の `StopAsync` / `RestartAsync` / `CompleteAsync` を await しない（デッドロックする。検出はしない）。`OnRequestEnded` から `EmitAsync` を呼ばない。

### リポジトリの構成の変更

- 2018 年版の原型のソースとビルド用のプロジェクトを `legacy/` に移しました（削除はしていません。原型の特性テスト 11 件は維持）。`master` ブランチは変更していません。

---

### 検証

WPF ベースの TCP 通信アプリケーションと WPF のサンプルアプリケーションで、ビルドと主要な実行経路を確かめました。検証した環境は Windows と .NET 10 だけです。ほかの OS、.NET のバージョン、構成での動作は確かめていません。

| 対象 | 結果 |
|---|---|
| このリポジトリのテスト | 207 件（原型の特性 11、ライブラリ 101、通信 83、デモ 12）が Debug / Release ともすべて成功 |
| WPF ベースの TCP 通信アプリケーション | この版の Eiii を参照してビルドし、Debug / Release ともエラーなし。127.0.0.1 の模擬サーバーを相手に、接続・送信・応答・無応答・切断・終了を画面の操作で確かめ、すべて期待どおり |
| WPF のサンプルアプリケーション | この版の Eiii のソースに差し替え、アプリとテストのコードを変更せずにビルドし、エラー 0・警告 0。Worker の検証コンソール（67 項目）と、画面の操作（実行、キャンセル、キャンセル後の再実行、イベントの連続投入、実行中やキューに要求が残った状態での終了）を Debug / Release で確かめ、すべて期待どおり |

どちらのアプリケーションも、この版で削除した API は使っていませんでした。TCP 通信アプリケーションは変更された `LinkTo` を使っていますが、1 引数の直接呼び出しなので、ソースを変更せずに再ビルドできました。そのため、どちらも移行の作業は不要でした。これは、確かめた範囲で問題が見つからなかったことを示すもので、すべての利用方法での互換性を保証するものではありません。
