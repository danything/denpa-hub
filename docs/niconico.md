# ニコニコ OAuth の中継

denpa の利用者が、自分のニコニコのアカウントでニコニコ実況に書き込めるようにするための中継です。
ドワンゴから Doa (ツール名 denpa) に発行された OAuth のクライアントで認可を取り、受け取ったトークンをその家の denpa へ渡します。

コードは `src/DenpaHub/Features/Niconico/` にあります。

## なぜ中継が要るか

- **denpa は家ごとにアドレスが違う。** `http://192.168.1.20:3000` のような LAN の IP で使う家も多く、OAuth のリダイレクト URI に全部を登録することはできません (登録できるのは 1 つか少数)。
- **シークレットを各家庭の denpa に配れない。** 配ればクライアント ID を第三者に使わせることになります。

そこで、リダイレクト URI とシークレットを持つ中継を Doa が 1 つ動かし、家の denpa とニコニコの間に入ります。中継は何も保存しません (トークンも、セッションも)。

## 書かないもの

ドワンゴから受け取った非公開の資料の中身 (エンドポイントのパス・スコープの名前など、ニコニコ固有の仕様) はリポジトリに書きません。
コードと文書は一般的な OAuth 2.0 (RFC 6749・RFC 7636 PKCE・RFC 7009 失効) の作りだけで書き、ニコニコ固有の値は[設定](#設定)で渡します。

## 流れ

```
denpa の画面         denpa-hub                     ニコニコ
    │ (1) GET /niconico/authorize?return=…&key=…
    │ ───────────────────▶ │
    │                      │ state を作る・Cookie を置く
    │ ◀─── 302 ─────────── │ (2) 認可の画面へ
    │ ─────────────────────────────────────────────▶ │ ログイン・同意
    │ ◀──────────────────────────────────────── 302 ─│ (3) GET /niconico/callback?code=…&state=…
    │ ───────────────────▶ │
    │                      │ (4) シークレットと PKCE でコードをトークンに替える ──▶ │
    │                      │ ◀──────────────────────────────────────────────────── │
    │ ◀─── 303 ─────────── │ (5) 戻り先#result=<denpa の鍵で暗号化したトークン>
    │ 画面がフラグメントを読み、denpa のサーバへ渡す → サーバが開いて保存する
```

更新は denpa のサーバから `POST /niconico/token` で中継を通します (シークレットが要るため)。

## エンドポイント

どの応答にも `Cache-Control: no-store`・`Referrer-Policy: no-referrer`・`X-Content-Type-Options: nosniff`・
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` を付けます。

### `GET /niconico/authorize`

denpa の画面がブラウザごと飛ばしてくる入口です。

| クエリ   | 説明                                                                                                     |
| -------- | -------------------------------------------------------------------------------------------------------- |
| `return` | 結果を返す先。`<denpa の origin>/settings/integrations/niconico/return` ([戻り先の検査](#戻り先の検査)) |
| `key`    | denpa のサーバが作った P-256 の公開鍵。非圧縮の 65 バイト (`0x04 ‖ X ‖ Y`) を base64url (パディングなし) |

- 通れば、Cookie `__Host-denpa-hub-niconico` を置いて、設定の認可のエンドポイントへ 302 で送ります。付けるパラメータは
  `response_type=code`・`client_id`・`redirect_uri`・`state`・`code_challenge`・`code_challenge_method=S256`・`scope` (設定にあれば) です。
- 通らなければ 400 と短い文 (text/plain) を返します。このときは戻り先がまだ信用できないので、リダイレクトはしません。

### `GET /niconico/callback`

ニコニコから戻ってくる先です (ドワンゴに登録するリダイレクト URI)。

1. `state` を開きます。読めない・改ざんされている・期限 (10 分) を過ぎている・Cookie が合わない ときは 400 と短い文を返します。
2. Cookie を消します。
3. `error` があれば (利用者が断ったなど)、`303 <戻り先>#error=<名前>` で返します。
4. シークレットと PKCE の `code_verifier` を付けてコードをトークンに替え、応答の JSON を denpa の公開鍵で暗号化して `303 <戻り先>#result=<暗号文>` で返します。
   応答の JSON は手を加えずに暗号化します (形が変わっても denpa が読むものだけ読めばよい)。`access_token` が無い応答は渡しません。

`#error=` の名前は、英小文字・数字・`_` だけにして 64 文字で切ったものです。

| 名前                      | どんなとき                                                    |
| ------------------------- | ------------------------------------------------------------- |
| `access_denied` など      | ニコニコが返した `error` (RFC 6749 4.1.2.1) をそのまま        |
| `invalid_grant` など      | トークンのエンドポイントが返した `error` をそのまま           |
| `token_exchange_failed`   | トークンのエンドポイントが `error` の無い失敗を返した         |
| `invalid_token_response`  | 応答に `access_token` が無い                                  |
| `temporarily_unavailable` | トークンのエンドポイントに届かない (15 秒で打ち切り)          |
| `invalid_request`         | `code` も `error` も無いコールバック                          |

### `POST /niconico/token`

denpa のサーバが、トークンを更新するときに呼びます (`application/x-www-form-urlencoded`)。

| フィールド      | 説明                    |
| --------------- | ----------------------- |
| `grant_type`    | `refresh_token` だけ    |
| `refresh_token` | 手元の refresh_token    |
| `scope`         | 任意。あればそのまま送る |

シークレットを付けてトークンのエンドポイントへ送り、**応答の状態コードと本文をそのまま返します** (`invalid_grant` なら denpa は連携をやり直してもらう)。
届かなければ `502 {"error":"temporarily_unavailable"}`。`grant_type` がほかのもの (コードとの交換を含む) は `400 {"error":"unsupported_grant_type"}` で断ります —
コードとの交換は、PKCE の verifier を持つコールバックの中だけで行います。

### `POST /niconico/revoke`

連携を外すときの失効 (RFC 7009)。フィールドは `token` と任意の `token_type_hint`。設定の `NICONICO_REVOKE_URL` が無ければ 404 です。
シークレットを付けて送り、応答の状態コードと本文をそのまま返します。

## state と PKCE

中継はサーバ側に何も持たないので、コールバックで要るものは全部 `state` に入れて往復させます。

- 中身: 組み直した戻り先・denpa の公開鍵・PKCE の `code_verifier` (32 バイトの乱数)・有効期限 (10 分)・Cookie の値の SHA-256
- 形: `base64url(バージョン 1 バイト ‖ nonce 12 バイト ‖ AES-256-GCM の暗号文 ‖ tag 16 バイト)`。鍵は `NICONICO_STATE_KEY`
- 暗号化するので `code_verifier` はブラウザにもニコニコにも見えず、改ざんすれば tag で見破れます。
- 台数を増やすときは、全部に同じ `NICONICO_STATE_KEY` を渡します。鍵を替えると、その時点で途中の連携 (最長 10 分) がやり直しになります。

`code_challenge` は `BASE64URL(SHA-256(code_verifier))` (S256) です。

## ブラウザとの結び付け

`/niconico/authorize` で、認可を始めたブラウザに Cookie `__Host-denpa-hub-niconico` (乱数。`Secure`・`HttpOnly`・`SameSite=Lax`・10 分) を置き、
その SHA-256 を `state` に入れます。コールバックでは同じ Cookie が来ないと断ります。
他人が自分で始めた連携のコールバックの URL を踏ませる、といった使い方を止めるためです (トークンは denpa の鍵で暗号化してあるので、
これが無くても他人の denpa には読めませんが、念のため両方で止めます)。

同じブラウザで連携を 2 つ同時に進めると、後から始めた方の Cookie で上書きされ、先の方はやり直しになります。

## トークンの渡し方

**denpa の公開鍵に宛てて暗号化し、戻り先の URL のフラグメント (`#result=`) に入れて返します。**

- フラグメントはブラウザから先へ送られないので、denpa の手前のプロキシのログや Referer に残りません。
- それでもブラウザの履歴や拡張機能には見えるので、中身は denpa のサーバだけが開けるように暗号化します。秘密鍵は denpa のサーバから出ません。
- 中継は何も保存せずに済みます。

ほかの方法を選ばなかったわけ:

| 方法                                                       | 選ばなかったわけ                                                                                                     |
| ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| トークンをそのままフラグメントに入れる                     | ブラウザの履歴・拡張機能・画面のスクリプトから読める                                                                 |
| 一度だけ使える受け取り番号を渡し、denpa のサーバが取りに来る | 中継がトークンを (短い間でも) 持つことになり、台数を増やすと共有の置き場が要る                                       |
| クエリに入れる                                             | denpa の手前のプロキシのログや Referer に残る                                                                        |

### 暗号の形

`result` は `base64url(使い捨ての公開鍵 65 バイト ‖ nonce 12 バイト ‖ 暗号文 ‖ tag 16 バイト)` です。

1. 中継は使い捨ての P-256 の鍵の組を作り、denpa の公開鍵と ECDH で共有の秘密 (32 バイト) を出す
2. 鍵 = HKDF-SHA256 (ikm = 共有の秘密、salt = 使い捨ての公開鍵 ‖ denpa の公開鍵、info = `denpa-hub niconico token v1`、長さ 32 バイト)
3. AES-256-GCM で暗号化する。AAD は戻り先の URL (`/niconico/authorize` に渡したものを組み直した形。下の注意) の UTF-8

denpa のサーバはこの逆をします (どれも WebCrypto でできます: `deriveBits` の ECDH → `HKDF` → `AES-GCM` の `decrypt`)。
テストの `Harness.Open` が同じ手順を C# で書いたものです。

## 戻り先の検査

戻り先はトークンを渡す先なので、任意の URL へ飛ばせると、罠のリンクから他人のトークンを外へ持ち出せてしまいます (オープンリダイレクト)。
**通すのは「denpa の決まったパス」かつ「インターネットからは受け取れないホスト」だけです。**

- スキームは `http` か `https`。ユーザー名・クエリ・フラグメントは付けられない。パスは `/settings/integrations/niconico/return` だけ
- ホストは次のどれか
  - IP アドレスで、ループバック (`127.0.0.0/8`・`::1`)・プライベート (`10.0.0.0/8`・`172.16.0.0/12`・`192.168.0.0/16`)・
    `100.64.0.0/10` (Tailscale などの VPN のアドレスもここ)・IPv6 のユニークローカル (`fc00::/7`)。IPv4 を写した IPv6 は IPv4 として見る
  - `localhost`、`.local` (mDNS)・`.home.arpa` (RFC 8375) で終わる名前 — 家の中でしか引けない名前
  - `NICONICO_ALLOWED_RETURN_HOSTS` に書いたホスト名 (https だけ)
- 制御文字・空白・`\`・ASCII でない文字を含むもの、512 文字を超えるものは断る
- 検査に通ったら、受け取った文字列ではなく**組み直した形** (`http://127.0.0.1/…` のように正規化したもの) だけを使う

名前で届くホスト (`denpa.example.jp`・Tailscale の `*.ts.net` など) は、既定では通しません。誰でも名前を取れ、公開もできるからです
(`*.ts.net` は Tailscale Funnel で公開できる)。こうしたアドレスで denpa を使っている家は、連携の操作だけ LAN のアドレスから開いてもらいます
(連携は一度だけの操作なので)。置く人が信用できるホストは `NICONICO_ALLOWED_RETURN_HOSTS` に書けます。

## レート制限

接続元のアドレスごとに 1 分の固定の枠で数えます (`TRUSTED_PROXIES` があれば `X-Forwarded-For` から取った利用者のアドレス)。超えたら 429 と短い文です。

| エンドポイント                                  | 上限       | わけ                                                                          |
| ----------------------------------------------- | ---------- | ----------------------------------------------------------------------------- |
| `/niconico/authorize`・`/niconico/callback`     | 30 回/分   | 人がブラウザで操作するもの                                                    |
| `/niconico/token`・`/niconico/revoke`           | 120 回/分  | 1 つの家は 1 時間に数回だが、CGNAT で多くの家が 1 つのアドレスを共有しうる |

## ログ

**残すもの:** 何が起きたか (`authorize: return=private`・`callback: delivered`・`callback rejected: binding`・`token: upstream 400` など)、
断ったわけの種類、ニコニコの状態コードと `error` の名前、例外の型の名前。

**残さないもの:** シークレット・コード・state・Cookie・トークン (アクセス・更新とも)・戻り先の URL (家の LAN のアドレス)・接続元のアドレス・
クエリ文字列。ASP.NET Core と HttpClient のログは URL を書くので、警告より下を出しません。
テスト (`ログにシークレットやコードやトークンやstateを書かない`) が、連携と更新を通したあとのログの全部にこれらが無いことを確かめます。

## 設定

どれも `<名前>_FILE` でファイルから読めます。機能をまたぐ設定 (`PUBLIC_ORIGIN`・`TRUSTED_PROXIES`) は [README](../README.md#設定) にあります。

| 環境変数                        | 要るか | 説明                                                                                                   |
| ------------------------------- | ------ | ------------------------------------------------------------------------------------------------------ |
| `NICONICO_CLIENT_ID`            | 要る   | ドワンゴから発行されたクライアント ID                                                                  |
| `NICONICO_CLIENT_SECRET`        | 要る   | クライアントのシークレット。`NICONICO_CLIENT_SECRET_FILE` でファイルから渡すのがよい                   |
| `NICONICO_AUTHORIZE_URL`        | 要る   | 認可のエンドポイント (ドワンゴの資料にあるもの)                                                        |
| `NICONICO_TOKEN_URL`            | 要る   | トークンのエンドポイント                                                                               |
| `NICONICO_REVOKE_URL`           | 任意   | 失効のエンドポイント。無ければ `/niconico/revoke` は 404                                               |
| `NICONICO_SCOPE`                | 任意   | 認可で求めるスコープ (空白区切り)。無ければ `scope` を付けない                                         |
| `NICONICO_TOKEN_AUTH`           | 任意   | `client_secret_basic` (既定。Basic 認証) か `client_secret_post` (フォームに入れる)。資料に合わせる     |
| `NICONICO_STATE_KEY`            | 要る   | state を暗号化する 32 バイトの鍵を base64 で (`openssl rand -base64 32`)。全部の台で同じものを使う      |
| `NICONICO_ALLOWED_RETURN_HOSTS` | 任意   | 戻り先として https で通すホスト名 (カンマ区切り)                                                       |

URL は https だけを受けます (手元で試すときのため、ループバックのホストだけは http も通します)。

## 置く人が用意するもの

- **ドワンゴに伝えるリダイレクト URI:** `<PUBLIC_ORIGIN>/niconico/callback` (例: `https://hub.example.jp/niconico/callback`)。起動時のログにも出ます。
- **ドワンゴに伝えるアイコン** (認可の画面に出るもの)。
- 上の[設定](#設定)の値。エンドポイントの URL・スコープ・クライアントの認証の方法は、ドワンゴの資料から選びます。
- ドメイン・TLS・置き場・シークレットの渡し方は [README](../README.md#置く人が用意するもの)。

## denpa 側が呼ぶ契約

1. 連携のボタンが押されたら、denpa のサーバが P-256 の鍵の組を作り、秘密鍵を利用者のセッションに 10 分だけ持つ。
   公開鍵は `exportKey("raw")` の 65 バイトを base64url にする。
2. 画面がブラウザごと `https://<denpa-hub>/niconico/authorize?return=<URL エンコードした戻り先>&key=<公開鍵>` へ移る。
   戻り先は `location.origin + "/settings/integrations/niconico/return"`。名前で開いているときは、LAN のアドレスで開き直すよう案内する。
   400 の文はそのまま利用者に見える。
3. `/settings/integrations/niconico/return` の画面は、`location.hash` から `result` か `error` を読み、すぐに `history.replaceState` でフラグメントを消し、
   `result` を denpa のサーバへ POST する。
4. denpa のサーバは[暗号の形](#暗号の形)の逆で開き (AAD は自分が渡した戻り先。中継は正規化した形を使うので、denpa も `http://<ホスト>[:<ポート>]/settings/integrations/niconico/return`
   の形で渡す)、中の JSON から `access_token`・`refresh_token`・`expires_in` などを読んで保存する。使った秘密鍵は捨てる。
5. 期限が近づいたら、denpa のサーバが `POST https://<denpa-hub>/niconico/token` (`grant_type=refresh_token&refresh_token=…`) で更新する。
   `400 invalid_grant` なら連携をやり直してもらう。
6. 連携を外すときは、`POST /niconico/revoke` (`token=…&token_type_hint=refresh_token`) を呼んでから手元のトークンを消す (404 なら失効は飛ばす)。

## 約款とのかかわり

- **クライアント ID を第三者に使わせない (第10条(5))。** シークレットは中継だけが持ち、各家庭の denpa にも利用者にも渡しません。
  ただし中継の入口はだれでも呼べるので、denpa 以外のソフトが同じ戻り先のパスを名乗って使うことは技術的には止められません
  (止めるには家ごとの登録が要ります)。決まったパス・LAN の戻り先・レート制限で、使える形を denpa に合わせて絞っています。
- **受け取った情報を外に出さない (第14条)。** トークンは保存せず、ログにも書きません。denpa へは denpa の鍵で暗号化して渡します。
- **ID・パスワードを保存しない。** ログインはニコニコの画面で行い、中継は ID もパスワードも見ません。
