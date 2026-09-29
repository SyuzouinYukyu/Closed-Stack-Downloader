# 📝 Changelog

## v1.0.2

### 🛡️ Resume安全性
- 保存済みFinalUrlと今回の最終到達FinalUrlをResume時に照合。
- Redirect先が変化した場合、Strong ETagが同一でも旧 `.part` へ追記しないよう修正。
- Resume不能時のFresh fallbackを1論理Attemptにつき最大1回へ制限。
- Retry=N の論理Attempt数を最大 N+1 に固定。
- Retry=0 + 不正206でも有限回で「失敗」へ終端するよう修正。

### 🧪 検証
- 既存19ケース + Resume回帰2ケース = 21 test cases。
- 独立検査ではChecksumなしのFinalUrl変更ケースも確認。

## v1.0.1

### 🔁 Resume
- `.part.meta` を導入。
- Strong ETag / If-Rangeを利用したResume安全化。
- Checksumなし・Sizeのみで安全なValidatorがない場合はFresh Download。
- 416でSize一致だけを根拠に完成扱いしないよう修正。

### 🧾 Referer
- 初回候補を空欄化。
- 実際に使用したRefererのみ履歴へ記憶。

### 🖥️ GUI
- DataGridViewの文字切れを修正。
- 保存先フォルダのDrag & Dropに対応。
- Checksumなしダウンロードの既定値をOFFへ変更。

## v1.0.0

- 初版。
- URLリスト一括ダウンロード。
- Referer対応。
- `.part` Resume。
- SHA-512 / BLAKE3 / Size検証。
- Manifest / `.sha512` / `.blake3` 対応。
- Pause / Resume / Stop / Retry。
- TOML設定保存。
- win-x64 Self-contained Single-file Publish。
