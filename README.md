# 🔐 プライバシーと透明性

**Closed_Stack_Downloader v1.0.2 は、動作に不要な外部通信や情報収集を行う機能を実装していません。**

本リポジトリは、ソフトウェアの透明性確保、実装内容の確認、および第三者による安全性検証を可能にすることを目的として、ソースコードを公開しています。

- 📡 外部通信は、ユーザーが指定したダウンロードURLへのHTTP(S)通信と、その正規のリダイレクト処理に限定しています。
- 🚫 テレメトリ、利用状況送信、広告、トラッキング、個人情報収集の機能は実装していません。
- 🔑 パスワード、Cookie、認証トークンを保存・送信する機能は実装していません。
- 🧾 Refererはユーザーが明示的に指定した場合のみ送信します。
- 💾 設定はローカルの `Closed_Stack_Downloader.toml` に保存されます。
- 🔍 ソースコードを公開し、通信・Resume・Checksum検証の実装内容を確認できるようにしています。

> 上記は **v1.0.2 時点の実装**についての説明です。

# Closed_Stack_Downloader

**Referer対応・レジューム転送・チェックサム検証を備えた、Closed Stack特化型の汎用一括ダウンローダー。**

Windows 11 x64 / .NET 10 / WinForms で動作する、1行1URL形式の一括ダウンロードツールです。Closed Stack向けの運用を主目的としていますが、通常のHTTP(S)ダウンロードURLにも利用できます。

## ✨ 主な機能

- 📄 1行1URLのTXTリストを一括処理
- 🧾 Referer送信 / 送信しないを選択可能
- 🕘 実際に使用したRefererだけを履歴へ保存
- ⏯️ 一時停止・再開・中止
- 🔁 `.part` + `.part.meta` を使用した安全なResume
- 🛡️ Strong ETag / If-Range / FinalUrl照合による誤Resume防止
- 🔐 HTTPS → HTTPへのリダイレクトを拒否
- 🔗 リダイレクト最大10回・ループ検出
- ✅ SHA-512 / BLAKE3 / Sizeによる完全性検証
- 📑 `.manifest` / `.sha512` / `.blake3` に対応
- 🚫 Checksumなしダウンロードは初期状態でOFF
- ♻️ Checksum不一致時は壊れた断片を使わずFresh Download
- 🧩 同時ダウンロード数 1～6
- 🔄 再試行回数・タイムアウト設定
- 📊 進捗・速度・残り時間・HTTP状態を表示
- 🖱️ URLリスト / Checksumファイル / 保存先フォルダのDrag & Drop
- 🔎 Ctrl + マウスホイールで9～16ptのフォント拡大縮小
- 🧱 Path Traversal・ファイル名衝突・不正なRange応答への防御
- 📦 win-x64 Self-contained / Single-file Publish対応

## 🖥️ 動作環境

- Windows 11 x64
- .NET 10
- WinForms

ソースからビルドする場合は .NET 10 SDK が必要です。

## 🚀 基本的な使い方

詳しい手順は **[📘 取扱説明書](docs/取扱説明書.md)** を参照してください。

1. 📄 URLリストTXTを指定
2. ✅ 必要に応じてChecksumファイルを追加
3. 📁 保存先フォルダを指定
4. 🧾 必要な場合だけRefererを入力
5. ⚙️ 同時Download数・再試行・タイムアウトを設定
6. ▶️ **ダウンロード開始**
7. 🔍 完了後の検証状態を確認

## 📄 URLリスト形式

UTF-8 / UTF-8 BOM付きのTXTを使用します。

```text
https://example.com/files/file01.mkv
https://example.com/files/file02.mkv
https://example.com/files/file03.zip
```

空行、`#` で始まる行、`;` で始まる行は無視します。

## ✅ Checksum

対応形式：

- `*.manifest`
- `*.sha512`
- `*.blake3`

SHA-512 / BLAKE3が存在する場合、ダウンロード後に実ファイルを検証し、一致した場合のみ検証済みとして確定します。

Checksumが存在しないURLのダウンロードは初期状態では無効です。明示的に有効化した場合は、完了状態を **「完了（未照合）」** と表示し、検証済みとは扱いません。

## ⏯️ Resumeの安全設計

Resumeでは単純なRange追記だけを行いません。

- `.part` と `.part.meta` を使用
- Strong ETagを利用可能な場合は `If-Range` を送信
- 保存済みFinalUrlと今回のFinalUrlを照合
- `Content-Range` の開始位置を検証
- FinalUrl / ETag / Range条件が不正なら旧断片へ追記せずFresh Download
- Checksum不一致時もFresh Download
- Retry回数は有限で、異常応答が続いても無限に再取得しません

これにより、古い断片と別内容のファイルを誤って結合して完成扱いすることを防ぎます。

## 🌐 ネットワーク動作

本アプリが行う通信は、ユーザーが指定したHTTP(S) URLへのダウンロード通信です。

- `Accept-Encoding: identity`
- User-Agent: `Closed_Stack_Downloader/1.0.2`
- Referer: ユーザーが使用を選択した場合のみ
- HTTPS → HTTP downgrade: 拒否
- Cookie / Authorization: 未使用
- テレメトリ / アナリティクス: 未実装

詳細は **[🛡️ SECURITY.md](SECURITY.md)** を参照してください。

## 🛠️ ビルド

```powershell
dotnet restore
dotnet test
dotnet build -c Release
dotnet publish .\Closed_Stack_Downloader\Closed_Stack_Downloader.csproj -c Release -r win-x64
```

Publish設定：

- `SelfContained=true`
- `PublishSingleFile=true`
- `IncludeNativeLibrariesForSelfExtract=true`
- `PublishTrimmed=false`

## 🧪 テスト

リポジトリには、URL解析、Checksum、Referer、Resume、Range、FinalUrl、Retry上限、設定保存などのRegression Testを含みます。

v1.0.2: **21 test cases**

```powershell
dotnet test
```

## 📦 v1.0.2 公式ビルド情報

- EXE size: `116,385,727 bytes`
- SHA-256: `B3248A472C752BB8C51D3281FE5060ED51C3CAA43CC20A0339D6D43FDC0A6A66`
- FileVersion: `1.0.2.0`

> Gitリポジトリではソースコードとドキュメントを中心に管理しています。配布用バイナリはGitHub Releases等での配布を推奨します。

## 🗂️ 構成

```text
Closed_Stack_Downloader.sln
├─ Closed_Stack_Downloader/   WinForms GUI
├─ Downloader.Core/           Download / Checksum / Resume / Settings
├─ Downloader.Tests/          Regression Tests
├─ docs/                      取扱説明書
├─ SECURITY.md
├─ CHANGELOG.md
└─ .gitignore
```

## 📝 ライセンス

本リポジトリには現時点でオープンソースライセンスを付与していません。  
ライセンスが明示されていない場合、著作権者の権利が保持されます。

## ⚠️ 注意

本ツールは、ユーザーが正当にアクセス権を持つURL・コンテンツに対して使用してください。  
アクセス制御の回避や、権利のないコンテンツの取得を目的とした機能は提供していません。
