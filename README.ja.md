<div align="center">

[简体中文](README.md) | [繁體中文](README.zh-TW.md) | [English](README.en.md) | **日本語** | [Türkçe](README.tr.md) | [Español](README.es.md) | [Čeština](README.cs.md)

![Banner](docs/images/DiscImageStudio_Banner_2.png)

<h2>光ディスクの記録面に可視画像を生成・書き込みするオールインワンツール</h2>

Disc Image Studio は、あらゆる記録ドライブを使って、あらゆる DVD / CD / 単層 Blu-ray ディスクの記録面に肉眼で見える画像を刻むための、マルチプラットフォーム対応のディスク記録面画像生成・書き込みツールです。

![.NET バージョン](https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square)
![対応プラットフォーム](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-0078d6?style=flat-square)
![開発言語](https://img.shields.io/badge/Language-C%23-68217A?style=flat-square)
![GitHub リポジトリサイズ](https://img.shields.io/github/repo-size/JiaFeiMiao-K-Cat/DiscImageStudio?style=flat-square&color=3cb371)
![コントリビューション歓迎](https://img.shields.io/badge/PRs-Welcome-brightgreen?style=flat-square)
[![ライセンス：GPL-3.0-or-later](https://img.shields.io/badge/License-GPL--3.0--or--later-blue?style=flat-square)](LICENSE)

</div>

---

## 作例

![](docs/images/case1.jpg)

![](docs/images/case2.jpg)

---

## 概要

Disc Image Studio は、1 枚の画像を CD、DVD、片面一層 BD-R ディスクの物理層の制約を満たすデータに変換します。書き込み後、ディスクの記録面に肉眼で見える模様が浮かび上がります。

## 画面プレビュー

![ホーム：テンプレートと最近のプロジェクト](docs/images/ui-home.png)

![デザイン：レイヤーキャンバスとトーン調整](docs/images/ui-design.png)

![エクスポート：ライトテーマと出力イメージ](docs/images/ui-export.png)

---

## クイックスタート

Windows x64 では `win-x64.exe`、Windows 11 ARM64 ではネイティブ版の `win-arm64.exe` で終わるファイルをダウンロードして実行します。Linux または macOS では、CPU に合った**実験的な CLI** のアーカイブを展開し、`./disc-image-studio --help` を実行してください。いずれも .NET ランタイムを同梱しています。[GitHub Releases](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases)

### 1. プロジェクトを新規作成

ホームで CD、DVD、Blu-ray のいずれかを選び、出力先を指定してプロジェクトを作成します。

### 2. 記録面をデザイン

デザインページで画像を読み込み、レイヤーでレイアウトを整え、右側の「トーン」パネルでディザリング、ガンマ、明るさ、階調数を調整します。

### 3. プレビューとエクスポート

プレビューページで記録面への投影を確認し、エクスポートページで ISO / RAW / WAV+CUE を生成するか、即時書き込み機能で空のディスクに直接書き込みます。

即時書き込みは実験的な機能です。廃棄してもよいテスト用メディアのみを使用してください。

## 機能

### ディスクデータの生成

- **CD 対応**：あらゆる CD に対応します。フォーマットヘッダー付きの WAV 音声トラックと対応する CUE をエクスポートでき、cdrecord、ImgBurn などの書き込みソフトと互換性があります。RAW 音声トラックのエクスポートも可能です。詳しくは [CD 書き込み互換性の説明](docs/CD_AUDIO_COMPATIBILITY.md)をご覧ください。
- **DVD 対応**：あらゆる DVD に対応します。ISO イメージをエクスポートできます。
- **Blu-ray 対応**：あらゆる片面一層 BD-R 25 GB に対応し、描画のみのモードと UDF 2.01 ファイルをサポートします。詳しくは [ADDING_BLURAY.md](docs/ADDING_BLURAY.md) をご覧ください。
- **ディスク同梱ファイル**：CD／DVD／Blu-ray では、保存するフォルダーと内周／外周の配置をデザインページで直接選択でき、エクスポートページでも調整できます。設定はプロジェクトとともに保存されます。
- **ファイル領域プレビュー**：ハイブリッドディスクのデザイン投影と完成品の読み戻しでは、ファイル領域（ファイルシステムを含む）が青色で示されます。
- **メディア別の階調方式**：CD と DVD はいずれも白黒 2 階調を使用し、CD は `0D / AA`、DVD はスクランブル領域で `A5 / 92` を使用します。詳しくは [PHYSICAL_MODEL_AUDIT.md](docs/PHYSICAL_MODEL_AUDIT.md) をご覧ください。
- **階調ディザリングツール**：独立したプログラムで、元画像を `0/255` の白黒 2 階調 PNG に変換することもできます。元画像を開く／ドラッグして読み込む操作、並べてのプレビュー、ロスレス PNG エクスポートに対応しています。詳しくは[使い方とビルド方法](tools/FourLevelDither/README.md)をご覧ください。
- **ディスクプリセット**：CD／DVD／BD にわたる複数ブランドのディスクパラメーターを内蔵しています。ご自身でキャリブレーションしたデータをご提供いただくことも歓迎します。詳しくは[プリセットのメンテナンス](docs/DISC_PRESETS.md)をご覧ください。

### 補助キャリブレーション

Disc Image Studio は、インテリジェントで高精度な補助キャリブレーション手順を備えています。目視による手動キャリブレーションと比べ、補助キャリブレーションのアルゴリズムは、確定可能な記録面の物理パラメーターの精度を 10<sup>-6</sup> から 10<sup>-14</sup> へと向上させます。キャリブレーション前はトラックピッチが一定のため、十字線の放射方向の線がはっきりと曲がります。補助キャリブレーションで得た 3 次のトラックピッチパラメーターで再生成して書き込むと、線は放射方向の直線により近くなります。

| キャリブレーション前 · 一定トラックピッチ | キャリブレーション後 · 3 次トラックピッチ |
| --- | --- |
| ![キャリブレーション前の DVD 実写：放射方向の十字線がはっきり曲がっている](docs/images/verbatim-dvd-calibration-before.jpg) | ![キャリブレーション後の DVD 実写：放射方向の十字線が直線に近づいている](docs/images/verbatim-dvd-calibration-after.jpg) |
| `P20261004-133519.jpg` | `P20261004-135416.jpg` |


手順とパラメーターについては[補助キャリブレーションの説明](docs/CALIBRATION.md)をご覧ください。

### 書き込み

- **イメージファイル出力**：完全な ISO / RAW ファイルをエクスポートし、適切な書き込みソフトで手動で書き込めます。
- **即時ストリーミング書き込み（実験的）**：CD、DVD、BD-R のデータを生成しながら書き込み、完全なイメージは保存しません。BD-R は MMC のシーケンシャル書き込みを使用します。
- **空でないメディアは使用しないでください！**

## システム・デバイス要件

- x64 デスクトップ版には Windows 10 2004（2004/20H1、19041）以降、または Windows 11 が必要です。ARM64 版には Windows 11 が必要です。ARM64 のネイティブ CI はソフトウェアの自己テストと GUI スクリーンショットを確認しますが、実ディスクへの書き込みは未検証です。
- ビルド：.NET 10 SDK（`global.json` で 10.0.x に固定）。自分でビルドした非自己完結型の GUI には .NET 10 Windows デスクトップランタイムが必要です。Release EXE にはランタイムが同梱されているため、.NET を別途インストールする必要はありません。
- 「即時書き込み」機能には、Windows が認識できる CD / DVD / BD 書き込みドライブと空のディスクが必要です。BD-R にはシーケンシャル書き込みに対応した空の片面一層ディスクが必要です。

## ビルドと実行

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DiscImageStudio.App --configuration Release
```

ビルドの回帰チェック：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

バージョン規則、CI による EXE 生成、Release ドラフトの確認、MSIX パッケージングと署名の手順については[リリースガイド](docs/RELEASING.md)をご覧ください。

## コマンドライン

glibc を使用する Linux では CPU に合わせて `linux-x64` または `linux-arm64` を選びます。Apple シリコン搭載 Mac では `osx-arm64`、Intel 搭載 Mac では `osx-x64` を選んでください。CLI で生成したイメージや音声トラックは、対応する書き込みソフトでディスクに書き込めます。

.NET 10 SDK を使ってソースから起動するには、次のコマンドを実行します。

```sh
dotnet run --project src/DiscImageStudio.Cli -c Release -- --help
```

CLI は PNG、JPEG、BMP、GIF を読み込み、アニメーション画像には最初のフレームを使用します。次の例では、2 セクター分の音声トラックと CUE シートを生成して動作を確認できます。

```sh
./disc-image-studio cd-generate --input picture.png --output sample.wav --sectors 2
```

| コマンド | 説明 |
| --- | --- |
| `solve` | 固定の高速テクスチャマッピング（CW 方向）で DVD イメージを生成します。白黒テクスチャはディザリングで混合することもできます |
| `encode` | 単一の DVD ECC ブロックを NRZI チャネルレベルにエンコードします |
| `calibrate` | キャリブレーション投影：生成ジオメトリで DVD のソース画像をサンプリングし、実測ジオメトリで記録面に投影します |
| `simulate` | 実測半径に基づき、生成した DVD ISO の読み戻しをシミュレートします |
| `selftest` | DVD エンジンの決定論的な回帰セルフテストを実行します |
| `cd-generate` | ディスク同梱ファイルがない場合は WAV + CUE / RAW 音声トラックを生成し、`--data-dir` を指定した場合は CD データディスクの ISO を生成します |
| `cd-preview-warp` | キャリブレーション投影：実測ジオメトリで CD のソース画像を投影します |
| `cd-preview-track` | 既存の WAV または RAW CD-DA 音声トラックをキャリブレーション済みジオメトリでレンダリングします（読み戻しシミュレーション） |
| `bd-generate` | 完全な BD-R ISO を生成し、`<iso>.json` サイドカーを書き出します |
| `bd-info` / `bd-probe` / `bd-encode` | BD パラメーター、生ペイロードのプローブ、または単一 ECC クラスターの診断情報をエクスポートします |

各コマンドの全パラメーターは、対応するドキュメントをご覧ください。

## ソース構成

```text
src/DiscImageStudio.Cli/     Linux / macOS 用のオフライン CLI
src/DiscImageStudio.ImageIO/  WIC とクロスプラットフォームの画像コーデック
src/DiscImageStudio.Core/    メディア非依存のモジュール契約、プロジェクト/プリセットモデル、ジョブとコマンドカタログ
src/DiscImageStudio.Imaging/ CD/DVD/BD 共通の画像前処理とディザリング補助
src/DiscImageStudio.Burning/ Windows IMAPI2 のデバイス列挙、ストリームバッファー、安全な書き込みレイヤー
src/DiscImageStudio.Cd/      CD-DA 生成コアと CD モジュール
src/DiscImageStudio.Dvd/     DVD エンジンのアダプターモジュール
src/DiscImageStudio.BluRay/  BD-R ジオメトリ、17PP/ECC、3T/6T マッピング、ストリーミング生成と ISO
src/DiscImageStudio.Iso/     UDF 2.01 / ISO9660 ライター
src/DiscImageStudio.App/     WPF GUI、統合コマンドライン、ストア用リソース
src/DvdImageSolver/          分離を保った DVD エンコード/ソルバーエンジン
tests/                       サードパーティのテストフレームワークを使わないアーキテクチャ回帰テスト
packaging/                   MSIX マニフェストとパッケージングスクリプト
tools/                       独立した補助ツールとアセット生成スクリプト
docs/                        アーキテクチャ、エンジン、プリセット、リリースなどのドキュメント
```

## ドキュメント

| ドキュメント | 内容 |
| --- | --- |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | モジュールの抽象化、レイヤー間の境界と拡張ルール |
| [LOCALIZATION.md](docs/LOCALIZATION.md) | UI 言語の設定、システム言語のフォールバックと翻訳リソースのメンテナンス |
| [DVD_ENGINE.md](docs/DVD_ENGINE.md) | DVD NRZI 画像制約ソルバーの規約、エンコードチェーンとセルフテストの範囲 |
| [CD_AUDIO_COMPATIBILITY.md](docs/CD_AUDIO_COMPATIBILITY.md) | CD 音声トラックの形式、バイトオーダーとゴースト（多重像）のトラブルシューティング |
| [ADDING_BLURAY.md](docs/ADDING_BLURAY.md) | Blu-ray モジュールの物理モデル、生成/書き込みの流れと拡張ポイント |
| [DISC_PRESETS.md](docs/DISC_PRESETS.md) | 編集可能なディスクプリセットの階層型 JSON 形式とマージ規則 |
| [CALIBRATION.md](docs/CALIBRATION.md) | 写真補正（ディスク実物の最外縁と中心穴の縁を枠で指定）、曲線キャリブレーションと DVD の 3 次トラックピッチフィッティング |
| [PHYSICAL_MODEL_AUDIT.md](docs/PHYSICAL_MODEL_AUDIT.md) | CD / DVD 生成物理モデルの再検証 |
| [VALIDATION.md](docs/VALIDATION.md) | 各回のビルド、回帰テスト、実ディスクテストのローカル検証記録 |
| [RELEASING.md](docs/RELEASING.md) | GitHub Release、MSIX パッケージング、署名テストとストア申請の手順 |
| [CHANGELOG.md](CHANGELOG.md) | バージョンごとの変更履歴 |

## 参考規格

Disc Image Studio の実装と物理モデルは、主に以下の規格を参考にしています：

| メディア | 規格と版 | 本プロジェクトで採用している範囲 |
| --- | --- | --- |
| CD-R | [ECMA-394、第 1 版、2010 年 12 月](https://ecma-international.org/publications-and-standards/standards/ecma-394/) — *Recordable Compact Disc Systems CD-R Multi-Speed*（[公式 PDF](https://ecma-international.org/wp-content/uploads/ECMA-394_1st_edition_december_2010.pdf)） | CD-R メディア、マルチスピード記録および CD-DA との互換関係。本プロジェクトは CD-DA 音声トラックを生成します。 |
| DVD 共通フォーマット / DVD-ROM | [ECMA-267、第 3 版、2001 年 4 月](https://ecma-international.org/publications-and-standards/standards/ecma-267/) — *120 mm DVD – Read-only disk*（[公式 PDF](https://ecma-international.org/wp-content/uploads/ECMA-267_3rd_edition_april_2001.pdf)） | DVD のデータフレーム、ECC、EFMPlus エンコードおよび附属書 G の変換テーブル。記録型ディスクのメディア規則は別途 ECMA-359 を参照しています。 |
| DVD-R | [ECMA-359、第 1 版、2004 年 12 月](https://ecma-international.org/publications-and-standards/standards/ecma-359/) — *80 mm (1,46 Gbytes per side) and 120 mm (4,70 Gbytes per side) DVD Recordable Disk (DVD-R)*（[公式 PDF](https://ecma-international.org/wp-content/uploads/ECMA-359_1st_edition_december_2004.pdf)） | 片面一層 DVD-R のジオメトリ、セクター、誤り訂正とエンコード。規格は片面および両面メディアを含みますが、両面は二層を意味しません。 |
| BD-R | [ISO/IEC 30190:2021、第 3 版、2021 年 1 月](https://www.iso.org/standard/80770.html) — 120 mm 一層（25.0 GB）および二層（50.0 GB）記録型 BD の情報交換と記録の規格 | 実験モードでは一層 25 GB のトラック、セクター配置、誤り訂正とエンコードを採用しています。現在、二層ディスクには対応していません。 |

CD の共通エンコード部分については、[ECMA-130、第 2 版（CD-ROM、1996）](https://ecma-international.org/publications-and-standards/standards/ecma-130/)も参考にしています。

## 書き込みとメディアの安全に関する注意

- Disc Image Studio はドライバーやサービスをインストールしません。「即時書き込み」機能は Windows 標準の IMAPI2 を使用し、空でないメディアを拒否します。
- 「即時書き込み」機能には実ディスクでのフィードバックが一部寄せられていますが、デバイス、メディア、書き込み経路ごとの検証範囲はまだ限られており、実験的な機能です。廃棄してもよいテスト用メディアのみを使用してください。書き込みの中断、停電、生成速度の不足によってディスクが使用不能になる可能性は依然としてあります！
- 実際の見え方は、ディスク、書き込みドライブ、ファームウェア、ライトストラテジーによって異なります。
- ハイブリッドディスクでは、書き込み開始前にフォルダーをスキャンしてレイアウトを確定します。その間にソースファイルのサイズが変わるとジョブは安全に中止されますが、すでに書き込みが始まったディスクは使用不能になる可能性があります。
- BD-R のシーケンシャル書き込みでは、スペア領域による再割り当ては行われません。中断後、同じディスクへの書き込みは通常続行できません。

## プライバシーとセキュリティ

- Disc Image Studio はネットワークに接続せず、ファイルをアップロードせず、テレメトリも収集しません。詳しくは[プライバシーポリシー](PRIVACY.md)をご覧ください。
- セキュリティ上の問題を発見した場合は、公開 Issue で開示せず、[セキュリティポリシー](SECURITY.md)に従って GitHub のプライベート報告機能をご利用ください。

## コントリビューション

問題の報告やコードの投稿を歓迎します。開発環境の構築、モジュール境界のルール、テスト要件、Pull Request の流れについては[コントリビューションガイド](CONTRIBUTING.md)をご覧ください。

## ライセンス

本プロジェクトは [GNU GPL v3.0 or later](LICENSE)（`GPL-3.0-or-later`）の下で公開されています。CD インターリーブの実装には CDImage から移植・改変したコードが含まれており、全体として互換性のある GPL ライセンスで配布されます。バイナリや改変版を配布する際は、対応するソースコードの提供などの要件を遵守する必要があります。サードパーティのフォントとコンポーネントはそれぞれのライセンスに従います。詳しくは[ライセンスに関する説明](LICENSE-NOTICE.md)および[サードパーティに関する表示](THIRD-PARTY-NOTICES.txt)をご覧ください。アプリの「バージョン情報」ページでも全文をオフラインで確認できます。

## 謝辞

[arduinocelentano 氏の CDImage プロジェクト](https://github.com/arduinocelentano/cdimage)に感謝します。このプロジェクトは、CD 音声トラックを生成してディスクの記録面に可視画像を形成する方法を示しました。本プロジェクトの CD インターリーブ実装はその関連コードから移植し、さらに改変したものであり、CD の記録面に描画するという発想もそこから着想を得ています。CDImage の原作者およびコントリビューターの皆さまの成果は、本プロジェクトの重要な基盤となっています。

---

## Star History

<a href="https://www.star-history.com/?repos=jiafeimiao-k-cat%2Fdiscimagestudio&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=jiafeimiao-k-cat/discimagestudio&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=jiafeimiao-k-cat/discimagestudio&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=jiafeimiao-k-cat/discimagestudio&type=date&legend=top-left" />
 </picture>
</a>
