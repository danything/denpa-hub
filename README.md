# denpa-hub

各家庭の [denpa](https://github.com/danything/denpa) から使う、Doa が動かす共通のサーバです。
いまの機能は、ニコニコ OAuth の中継と予約数ランキングです。

denpa は家ごとに置く録画サーバなので、家ごとにアドレスが違い、サービスから受け取った秘密 (OAuth のクライアントのシークレットなど) も配れません。
そうした「1 か所で持つ必要があるもの」をここに集めます。画面は持たず、エラーのときだけ短い文を返します。

| 機能                       | 説明                                                                                                  | 文書                                 |
| -------------------------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------ |
| ニコニコ OAuth の中継      | denpa の利用者が自分のニコニコのアカウントでニコニコ実況に書き込めるよう、認可・トークンの更新・実況の視聴の接続先を中継する | [docs/niconico.md](docs/niconico.md) |
| 予約数ランキング           | 参加した denpa の予約を放送ごとに数え、よく予約されている番組の順位を返す。hub の 1 段階目 (トークン・票・順位) まで。denpa からはまだ使っていない | [docs/ranking.md](docs/ranking.md)   |

機能は `src/DenpaHub/Features/<名前>/` にまとめ、`Hub.cs` で足します。機能をまたぐもの (ログの出力レベル・応答のヘッダ・エラーの JSON・プロキシ・
レート制限の断り方・`/healthz`) だけが `Hub.cs` にあります。

## 置く人が用意するもの

- ドメインと TLS。例: `https://hub.example.jp`。TLS は手前のリバースプロキシ (Ingress など) で終端し、このサーバは 8080 番で http を受けます。
  HSTS もプロキシで付けます。
- 置き場。コンテナ 1 つ。イメージは `ghcr.io/danything/denpa-hub:<タグ>`。ニコニコの中継は状態を持たないので、何台並べても同じ設定で動きます。
  予約数ランキングを入れるときは、書けるボリュームを付けて 1 台だけで動かします ([docs/ranking.md](docs/ranking.md#データの持ち方))。
- シークレットの渡し方。環境変数か、`<名前>_FILE` でファイルの場所を渡します (Kubernetes の Secret をファイルで載せるなど)。
  シークレットの値はリポジトリ・ログ・Issue のどこにも書きません。
- 機能ごとに要るもの (ニコニコはリダイレクト URI の登録など) は、各機能の文書の「置く人が用意するもの」にあります。

## 設定

機能をまたぐ設定です。機能ごとの設定は各機能の文書にあります。どれも `<名前>_FILE` でファイルから読めます。

| 環境変数          | 要るか | 説明                                                                                                               |
| ----------------- | ------ | ------------------------------------------------------------------------------------------------------------------ |
| `PUBLIC_ORIGIN`   | 要る   | 外から見えるこのサーバの origin。`https://hub.example.jp` のようにパスを付けない。リダイレクト URI はここから決まる |
| `TRUSTED_PROXIES` | 任意   | `X-Forwarded-For` を信じる手前のプロキシ (CIDR のカンマ区切り)。手前が何段もあるときは全段の範囲を書く (Cloudflare を通すなら Cloudflare の範囲も)。右から順に範囲内のアドレスを外し、最初の範囲外を利用者のアドレスとする。空なら接続元のアドレスをそのまま使う (レート制限の単位) |

設定に誤りがあると、名前だけを書いて (値は書かずに) 終了コード 1 で止まります。

## 動かし方

```sh
docker run --rm -p 8080:8080 \
  -e PUBLIC_ORIGIN=https://hub.example.jp \
  -e NICONICO_CLIENT_SECRET_FILE=/run/secrets/niconico-client-secret \
  ... \
  ghcr.io/danything/denpa-hub:v0.4.0
```

イメージは Native AOT の実行ファイル 1 個を distroless (`gcr.io/distroless/cc-debian13:nonroot`) に置いたもので、シェルもパッケージ管理も無く、root でも動きません。
`GET /healthz` が `ok` を返します (生存確認用)。

### Doa のクラスタ (`https://dh.doany.io`)

`deploy/` が Doa のクラスタに置くマニフェストです。danything/gitops の bootstrap の ApplicationSet が `deploy/argocd.yaml` を見つけて、
main の `deploy/*.yaml` をそのまま同期します (Helm は使わない)。

| ファイル              | 中身                                                                                              |
| --------------------- | ------------------------------------------------------------------------------------------------- |
| `argocd.yaml`         | ApplicationSet が読む、この置き場の設定 (namespace `denpa-hub`)                                   |
| `namespace.yaml`      | PSA を restricted にするラベル                                                                    |
| `deployment.yaml`     | Deployment・Service・ランキングのデータの PVC。`PUBLIC_ORIGIN`・`TRUSTED_PROXIES`・`RANKING_DATA` とイメージ (タグと digest) はここ |
| `secrets.sops.yaml`   | SopsSecret。SOPS で暗号化した値から、sops-secrets-operator が Secret `denpa-hub` を作る |
| `live.sops.yaml`      | SopsSecret。実況の番組と視聴の接続先を引く設定の Secret `denpa-hub-live`                    |
| `httproute.yaml`      | Gateway `kube-system/doany` の `https` のリスナーに `dh.doany.io` を載せる (Cloudflare を通す)    |
| `network-policy.yaml` | 届く相手を Gateway とノードに絞る CiliumNetworkPolicy                                             |

`secrets.sops.yaml` には、[docs/niconico.md](docs/niconico.md#設定) の `NICONICO_*` を同じ名前で入れます
(シークレットに加えて、平文で書かないニコニコ固有のエンドポイント・スコープ)。実況の番組と視聴の接続先を引く URL・応答の形は `live.sops.yaml` に、
秘密ではない局の対応表 (`NICONICO_LIVE_CHANNELS`) は `deployment.yaml` にあります。値は暗号化してあり、直すときは danything/gitops の
`tools/t sops edit deploy/<ファイル>` で開きます (鍵は `.sops.yaml`)。
予約数ランキングの印の鍵 `RANKING_MARK_KEY` ([docs/ranking.md](docs/ranking.md#データの持ち方)) も `secrets.sops.yaml` にあります。
`deployment.yaml` は `RANKING_DATA` を渡すので、鍵を消すとランキングの入ったイメージは起動しません。
ghcr.io のパッケージは公開なので、引くための資格情報は要りません。

#### 新しいバージョンを出すとき

1. `v*` のタグを push する。CI の `image` のジョブがイメージを ghcr.io に push する
2. そのジョブの「Build (and push on tags)」が出力した digest (`docker buildx imagetools inspect ghcr.io/danything/denpa-hub:<タグ>` でも分かる) を、
   タグと一緒に `deployment.yaml` の `image` に書き、PR にして main に入れる。ArgoCD が main を同期する
3. 入れ替えは `Recreate` なので、古い Pod が止まってから新しい Pod が同じ PVC を開く。その間は応答しない
4. ランキングの表の形を変えたバージョン (`RankingStore` の `SchemaVersion`) は、起動したときに中身を捨てて作り直す。denpa がトークンを取り直して送り直せば戻るので、移す手順は要らない

## 開発

.NET 11 の SDK で組みます。手元に SDK を入れない場合は、コンテナで動かします。

```sh
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:11.0 \
  sh -c "dotnet build denpa-hub.slnx -warnaserror && dotnet test --project tests/DenpaHub.Tests"
docker build -t denpa-hub:local .
```

テストは偽のニコニコ (OAuth 2.0 の認可サーバと、パスも応答の形も偽物の実況の API) を同じプロセスに立て、連携の全体 (認可 → 同意 → コールバック → トークンの交換 → 暗号化して返す) と、
更新・実況の接続先の中継・戻り先の検査・ログに秘密が出ないことを確かめます。
ランキングは一時の場所に SQLite のファイルを作り、トークンの発行から票・順位・取り消し・消す処理までを確かめます。

CI (`.github/workflows/ci.yml`) は PR と main で build・テスト・イメージの組み立てを回し、`v*` のタグを push したときだけイメージを ghcr.io に push します。
PR には Claude が自動でレビューを付けます (`.github/workflows/claude-code-review.yml`。draft・bot・fork の PR と、main を取り込んだだけの push は見ない)。

## ライセンス

[AGPL-3.0-or-later](LICENSE) (denpa と同じ)。
