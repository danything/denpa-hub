# denpa-hub

各家庭の [denpa](https://github.com/danything/denpa) から使う、Doa が動かす共通のサーバです。
いまの機能はニコニコ OAuth の中継です。

denpa は家ごとに置く録画サーバなので、家ごとにアドレスが違い、サービスから受け取った秘密 (OAuth のクライアントのシークレットなど) も配れません。
そうした「1 か所で持つ必要があるもの」をここに集めます。画面は持たず、エラーのときだけ短い文を返します。

| 機能                       | 説明                                                                                                  | 文書                                 |
| -------------------------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------ |
| ニコニコ OAuth の中継      | denpa の利用者が自分のニコニコのアカウントでニコニコ実況に書き込めるよう、認可とトークンの更新を中継する | [docs/niconico.md](docs/niconico.md) |

機能は `src/DenpaHub/Features/<名前>/` にまとめ、`Hub.cs` で足します。機能をまたぐもの (ログの絞り・応答のヘッダ・プロキシ・
レート制限の断り方・`/healthz`) だけが `Hub.cs` にあります。

## 置く人が用意するもの

- **ドメインと TLS。** 例: `https://hub.example.jp`。TLS は手前のリバースプロキシ (Ingress など) で終端し、このサーバは 8080 番で http を受けます。
  HSTS もプロキシで付けます。
- **置き場。** コンテナ 1 つ (状態を持たないので、何台並べても同じ設定で動きます)。イメージは `ghcr.io/danything/denpa-hub:<タグ>`。
- **シークレットの渡し方。** 環境変数か、`<名前>_FILE` でファイルの場所を渡します (Kubernetes の Secret をファイルで載せるなど)。
  シークレットの値はリポジトリ・ログ・Issue のどこにも書きません。
- 機能ごとに要るもの (ニコニコはリダイレクト URI の登録など) は、各機能の文書の「置く人が用意するもの」にあります。

## 設定

機能をまたぐ設定です。機能ごとの設定は各機能の文書にあります。どれも `<名前>_FILE` でファイルから読めます。

| 環境変数          | 要るか | 説明                                                                                                               |
| ----------------- | ------ | ------------------------------------------------------------------------------------------------------------------ |
| `PUBLIC_ORIGIN`   | 要る   | 外から見えるこのサーバの origin。`https://hub.example.jp` のようにパスを付けない。リダイレクト URI はここから決まる |
| `TRUSTED_PROXIES` | 任意   | `X-Forwarded-For` を信じる手前のプロキシ (CIDR のカンマ区切り)。空なら接続元のアドレスをそのまま使う (レート制限の単位) |

設定に誤りがあると、名前だけを書いて (値は書かずに) 終了コード 1 で止まります。

## 動かし方

```sh
docker run --rm -p 8080:8080 \
  -e PUBLIC_ORIGIN=https://hub.example.jp \
  -e NICONICO_CLIENT_SECRET_FILE=/run/secrets/niconico-client-secret \
  ... \
  ghcr.io/danything/denpa-hub:v0.1.0
```

イメージは Native AOT の実行ファイル 1 個を distroless (`gcr.io/distroless/cc-debian13:nonroot`) に置いたもので、シェルもパッケージ管理も無く、root でも動きません。
`GET /healthz` が `ok` を返します (生存確認用)。

## 開発

.NET 10 の SDK で組みます。手元に SDK を入れない場合は、コンテナで動かします。

```sh
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c "dotnet build denpa-hub.slnx -warnaserror && dotnet test --project tests/DenpaHub.Tests"
docker build -t denpa-hub:local .
```

テストは偽の OAuth 2.0 の認可サーバを同じプロセスに立て、連携の全体 (認可 → 同意 → コールバック → トークンの交換 → 暗号化して返す) と、
更新・失効・戻り先の検査・ログに秘密が出ないことを確かめます。

CI (`.github/workflows/ci.yml`) は PR と main で build・テスト・イメージの組み立てを回し、`v*` のタグを push したときだけイメージを ghcr.io に push します。

## ライセンス

[AGPL-3.0-or-later](LICENSE) (denpa と同じ)。
