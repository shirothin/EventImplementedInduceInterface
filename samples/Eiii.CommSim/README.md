# Eiii.CommSim — 通信シミュレーター

通信機器から断片的に届く文字列（チャンク）を、**受信 Worker** がフレームにまとめ、**解析 Worker** が解析する。この流れを、実機を使わずに再現して確かめるシミュレーターです。7 つのシナリオを順に実行してログを表示するほか、**任意の電文を投入して 3 つの方式を比較する `custom` モード**があります。

- 部品（フレーム確定・解析・Worker・模擬機器）は [`samples/Eiii.Comm`](../Eiii.Comm) にあります（名前空間は `Eiii.CommSim`）。このプロジェクトには、シナリオを並べた `Program.cs` と、`custom` モードの処理（`Custom/`）があります。
- 通信に固有の処理はすべてこちら側にあり、ライブラリ本体（`src/Eiii`）は通信の知識を持ちません。

```
dotnet run --project samples/Eiii.CommSim              # 全シナリオ（約 18 秒）
dotnet run --project samples/Eiii.CommSim -- delimiter # A～C だけ
dotnet run --project samples/Eiii.CommSim -- timeout   # D～G だけ
dotnet run --project samples/Eiii.CommSim -- custom --file osd.txt   # 任意の電文で 3 方式を比較（後述）
```

---

## 2 つの方式と、使い分け

| 方式 | 想定するコマンド | フレームが確定する条件 | 部品 |
|---|---|---|---|
| **デリミタ方式** | 通常のコマンド応答（CR などの終端がある） | 区切り文字を検出したとき | `DelimiterFramer`、`ReceiverTask`、`ParserTask` |
| **時間経過方式** | OSD 取得などの特殊コマンド（応答の内部に複数の CR を含み、全体の終端マーカーがない） | 時間の条件を満たしたとき | `TimeoutFramer`、`TimedReceiverTask`、`OsdParserTask` |

**コマンドの種類に応じてどちらを使うかは、アプリケーション側が決めます。** OSD 応答を CR で区切ると、1 つの応答がばらばらの行に分かれてしまうため、時間の条件でまとめます。

### デリミタ方式の流れ

```mermaid
flowchart LR
    Dev["DeviceSimulator<br/>WriteAsync（満杯なら待つ）"] -- "string（チャンク）" --> RQ[("Receiver の Channel")]
    RQ --> R["ReceiverTask<br/>EiiiWorker(string → Frame)<br/>DelimiterFramer を所有"]
    R -- "Frame（EmitAsync）" --> PQ[("Parser の Channel")]
    PQ --> P["ParserTask<br/>EiiiWorker(Frame → Reading)"]
    P -- "Reading" --> CB["TaskToCallback（ログ表示）"]
```

- `DelimiterFramer` は、チャンクの境界とフレームの境界を無関係に扱います。区切り文字（既定は CR LF）がチャンクをまたいでも検出します。
- 区切り文字のない末尾は、正常終了（`CompleteInput`）のときに `IsComplete = false` のフレームとして渡し、Parser はエラー付きの結果を出します（捨てません）。キャンセル停止のときは framer に残り、再起動後に届くチャンクとつながります。

### 時間経過方式の 3 つの時間条件

| 条件 | 測る区間 | 満たしたとき | シナリオでの値 |
|---|---|---|---|
| `IdleTimeout`（Idle 方式） | **最後の受信**からの無通信時間 | フレームを確定（`IdleTimeout`） | 250ms |
| `CollectionTimeout`（Fixed 方式） | **最初の受信**からの経過時間 | フレームを確定（`CollectionTimeout`）。秒単位の打ち切り | 2 秒 |
| `ResponseTimeout` | 要求から**最初の受信**まで | `NoResponse` を通知（データなし）。フレーム確定の閾値ではない | 6 秒 |
| `MaxFrameLength` | 確定前のデータの長さ | そこまでのデータ全体を `Overflow` として出す（捨てない） | 1,024 文字 |

Idle 方式と Fixed 方式を両方指定すると、先に満たした方で確定します。これらの値は記憶に基づく概算で（約 300 文字、断片間は数十～100ms、応答まで最大約 5 秒）、実機で確かめた仕様ではありません。

```mermaid
sequenceDiagram
    autonumber
    participant Dev as 機器（OsdSimulator）
    participant TS as TickSource（PeriodicTimer）
    participant Q as Receiver の Channel（到着順）
    participant W as TimedReceiverTask
    participant F as TimeoutFramer

    Dev->>Q: Chunk(text, ReceivedAt=到着した時刻)
    TS->>Q: Tick(Now=生成した時刻)（満杯なら送らない）
    W->>Q: 到着順に取り出す（取り出した時刻は判定に使わない）
    W->>F: Feed(text, ReceivedAt)（前の受信からの間欠が閾値以上なら、先に前のフレームを確定）
    W->>F: Poll(Tick.Now)（後続のデータが来なくても無通信を検出）
    F-->>W: TimedFrame（IdleTimeout / CollectionTimeout / Overflow / NoResponse）
```

**判定には、Chunk の受信時刻と Tick の生成時刻だけを使います。** Worker がキューから取り出した時刻は使いません。そのため、下流が詰まってキューで待たされても、フレームの境界は変わりません（テストで確認済み）。

---

## シナリオと実行結果

以下は実際の実行ログの要約です（時刻は実行のたびに変わります）。

| シナリオ | 操作 | 結果 |
|---|---|---|
| A. 正常終了 | `"TEMP=2"` `"3.5\r"` `"\nHUM=4"` … の順にチャンクを送り、`CompleteAsync` を Receiver → Parser の順に呼ぶ | TEMP=23.5、HUM=40、PRES=1013 に復元。区切り文字のない末尾 `TAIL=7` は「未完了フレーム」のエラー付き結果として届く |
| B. バックプレッシャー | Parser のキュー容量 1、解析 200ms で 6 件送る | 6 件目で機器の `WriteAsync` が約 200ms 待たされる。6 件すべてが順序どおりに届き、捨てたデータは 0 |
| C. キャンセル停止 | 4 フレームを送り、2 件目の解析中に `StopAsync` を Receiver → Parser の順に呼び、再起動してから正常終了 | 解析中だった C=2 だけが失われ、C=1、C=3、C=4 が解析される |
| D. Idle 方式 | 約 300 文字（内部に CR 12 個）を 10～100ms の間欠で送る | 1 つの応答に確定し、内容は CR を含めて送信と一致 |
| E. Idle 方式の誤分割 | D と同じ応答の途中に 400ms の間欠を入れる | 2 つ（150 文字ずつ）に分かれる。本来は 1 件 |
| F. Fixed 方式 | E と同じ応答を、収集時間 2 秒で受ける | 1 つにまとまるが、確定は最初の受信から 2 秒後（Idle 方式より遅い） |
| G. 応答なし | 要求だけを送り、機器は応答しない | 6 秒後に `NoResponse` |

### Idle 方式と Fixed 方式の比較

| 観点 | Idle 方式 | Fixed 方式 |
|---|---|---|
| 確定までの遅れ | 短い（応答が終わってから閾値 ＋ Tick の周期程度） | 長い（応答の長さに関係なく収集時間だけ待つ） |
| 応答途中の間欠 | 閾値以上なら誤って分割する | 収集時間内なら分割しない |
| 長い応答 | 間欠が閾値未満なら全体を 1 つにまとめる | 収集時間を超えた分は打ち切られて分割される |
| 途切れなく送り続ける相手 | 確定しない（収集時間の上限との併用が必要） | 必ず一定時間で確定する |

---

## 任意の電文を投入する（custom モード）

過去に実機から返ってきた電文に相当するものを投入し、**同じ分割・同じ間欠のまま**、Delimiter / Idle / Fixed の各方式でどう確定するかを比べるためのモードです。新しい通信機能は持たず、既存の部品（`ReceiverTask` / `DelimiterFramer`、`TimedReceiverTask` / `TimeoutFramer` / `TickSource`、`OsdSimulator.SendAsync`）に電文を流すだけです。

```
dotnet run --project samples/Eiii.CommSim -- custom --help
```

### 入力：`--text` と `--file`（どちらか一方）

| 指定 | 扱い |
|---|---|
| `--text <電文>` | エスケープ表記を解釈します：`\r`（CR）、`\n`（LF）、`\t`（TAB）、`\\`（\）、`\xHH`（U+00HH。16 進 2 桁）。それ以外の `\` の後の文字は誤りになります |
| `--file <パス>` | ファイルの内容を**そのまま 1 つの電文**として使います |

**ファイルの文字コードと改行の仕様**

- BOM があれば BOM に従います（UTF-8 / UTF-16 / UTF-32）。BOM がなければ **UTF-8 として厳密に読み**、UTF-8 として不正なバイトがあれば誤りにします。Shift_JIS などの別の文字コードや、バイナリの電文は対象外です。
- **CR / LF / CRLF は変換しません。** ファイルの中の改行がそのまま電文の一部になります。末尾の改行も取り除きません。
- 多くのエディターは、改行を CRLF や LF で保存したり、末尾に改行を付け足したりします。**CR だけの電文は、エディターで作るより、たとえば PowerShell で作るほうが確実です。**

```powershell
# CR だけを改行とするファイルを作る（.NET の既定は BOM なしの UTF-8）
# パスは $PWD 付きで指定する（Windows PowerShell 5.1 では .NET の相対パスの基準が現在の場所と異なることがある）
[IO.File]::WriteAllText("$PWD\osd.txt", "*** XPORT SETUP MENU ***`r0 Server configuration`r...`rYour choice ? ")
```

### 分割と間欠

| 指定 | 分割 | 間欠 |
|---|---|---|
| （何も指定しない） | 乱数（`--chunk-range`、既定 8-40 文字） | 乱数（`--gap-range`、既定 10-100ms） |
| `--split 60,130,200` | 指定した文字の位置で分割 | 乱数（`--gap-range`） |
| `--split 60,130,200 --gaps 40,300,60` | 指定した位置で分割 | 2 番目以降の各チャンクの前に、指定した ms の間欠を置く（個数 = チャンク数 - 1） |

乱数は `--seed`（既定 1）で再現できます。最初のチャンクの前の間欠は常に 0 です。`--gaps` だけを指定することはできません（チャンクの数が決まらないため）。

**`--split` の位置は、バイト位置ではなく文字列の位置**（.NET の文字列のインデックス。UTF-16 のコード単位）です。ASCII だけの電文ならバイト位置と同じですが、日本語などの非 ASCII 文字を含む電文では、UTF-8 のバイト位置とは一致しません。サロゲートペア（絵文字など）の途中も分割できてしまいます。チャンク長（`--chunk-range`）も同じく文字数で数えます。

### 方式の条件

`--mode all|delimiter|idle|fixed`（既定 all）、`--delimiter`（既定 `\r`）、`--idle`（既定 250ms）、`--collection`（既定 2000ms）、`--max`（既定 1,024 文字）、`--tick`（既定 25ms）。`--verbose` を付けると、チャンクの一覧も表示します。誤った引数を与えると、理由と使い方を表示して終了コード 2 で終わります。

### 実行例（実機の OSD 応答に相当する 271 文字・CR 12 個の電文）

乱数で分割した場合（`--file osd_xport.txt --seed 3`。チャンク 11 個、間欠 17～88ms）：

```
=== 比較（同じ分割・同じ間欠） ===
  Delimiter 区切り \r | フレーム 13 個 | Delimiter×12 / 残り(入力終了)×1 | 最後の確定 +569ms | 入力と一致: はい
  Idle      無通信 250ms | フレーム 1 個 | IdleTimeout×1 | 最後の確定 +812ms | 入力と一致: はい
  Fixed     収集 2000ms | フレーム 1 個 | CollectionTimeout×1 | 最後の確定 +2001ms | 入力と一致: はい
```

- **Delimiter**：OSD 応答の内部の CR で 1 行ずつに分かれます。最後の `Your choice ? ` には CR がないので、入力が終わったときの「残り」になります。
- **Idle**：間欠がすべて 250ms 未満なので、1 つにまとまります。最後のチャンクから約 250ms 後に確定します。
- **Fixed**：1 つにまとまりますが、確定は最初の受信から 2 秒後です。

途中に長い間欠を明示した場合（`--split 60,130,200 --gaps 40,300,60`）：

```
=== Idle（無通信 250ms） ===
    1  [IdleTimeout]   130 文字  CR   5  確定 +  305ms  "*** XPORT SETUP MENU ***\r0 Server c…"
    2  [IdleTimeout]   141 文字  CR   7  確定 +  710ms  "nect Mode C0, Disconn 00\r2 E-mail s…"
```

300ms の間欠が閾値（250ms）を超えたため、Idle 方式では 1 つの応答が 2 つに分かれました（`Con` / `nect` の単語の途中で切れています）。同じ入力でも、Fixed 方式は 1 つ（271 文字）のままでした。

> **実時間の判定には揺らぎがあります。** 間欠は `Task.Delay`、判定は Tick（`PeriodicTimer`）で実時間に行うため、OS のタイマー分解能やスレッドのスケジューリングによって、実際の間欠や判定の時刻は指定値から数 ms～数十 ms ずれます。時刻（「確定 +○ms」）は実行のたびに変わります。フレームの数と内容は、同じ分割と間欠であれば通常は変わりませんが、間欠が閾値に近い場合（たとえば 240～260ms）は、実行ごとに結果が変わり得ます。境界ちょうどの動作を確定的に確かめたい場合は、時刻を合成値で与える `TimeoutFramer` の単体テストを使います。

---

## 既知の制約

- **時間条件による確定は、電文の完全性を保証しません。** 終端マーカーがないため、応答を最後まで受け取ったかは確認できません。
- `MaxFrameLength` は厳密な出力長の上限ではなく、Overflow を検出する閾値です（超えた原因のチャンクまで含めて出力するので、わずかに超え得ます。残りは別のフレームになります）。
- Chunk と Tick は同じ Channel に到着順で入る前提です。送り手の `WriteAsync` 自体が満杯で待たされた場合の投入順序は、Channel の実装に依存し、**検証していません**（シナリオとテストでは、Receiver のキューを大きくしてこの状況を避けています）。
- 経過時間の判定に実時刻（`DateTimeOffset`）を使っているので、時計の補正の影響を受け得ます。
- 判定の粒度は、Tick の周期（25ms）と OS のタイマー分解能に左右されます。
- デリミタ方式のバッファには上限がありません。
- キャンセル停止では、処理中のフレームや、1 つのチャンクから確定した後まだ出力していないフレームが失われ得ます。

## テスト

`tests/Eiii.CommSim.Tests`（83 件）です。

- `DelimiterFramer` と `TimeoutFramer` の単体テスト：時刻は合成値を渡すので、実時間に依存しません。閾値ちょうど（249 / 250ms）の境界、誤分割、Overflow、NoResponse を確認します。
- 結合テスト（実時間）：Receiver と Parser の独立性、バックプレッシャー、停止と正常終了の違い、キューで待たされても受信時刻で判定されること。
- `custom` モード（49 件）：エスケープ表記の解釈と誤り、引数の解析と誤り（16 通り）、分割位置・間欠の明示と乱数の再現性、ファイルの読み込み（CR / LF / CRLF の保持、UTF-8 の BOM、不正なバイト、空のファイル）、3 方式の比較の実行。
- 実行結果：Debug / Release 構成とも 83 件すべて成功（2026-10-14）。
