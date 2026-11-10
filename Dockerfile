# ---------------------------------------------------------------------------
# denpa-hub。Native AOT で実行ファイル 1 個にし、distroless に置く。
#
# **arch ごとに、その arch のランナーで組む** (AOT のクロスはリンカの揃え直しが要るので、
# 組む段を `--platform=$BUILDPLATFORM` にはしない)。土台は digest で留める。
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG TARGETARCH
# 上流へ送る User-Agent に載せるバージョン (CI がタグの名前を渡す)
ARG VERSION=0.0.0
WORKDIR /build
# AOT のリンクに clang と zlib が要る
RUN apt-get update && \
    apt-get -y --no-install-recommends install clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*
COPY global.json ./
COPY src/DenpaHub/ src/DenpaHub/
RUN case "${TARGETARCH:-amd64}" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "対応していない arch: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/DenpaHub/DenpaHub.csproj -c Release -r "${rid}" -p:Version="${VERSION#v}" -o /out && \
    rm -f /out/*.dbg

# ---------------------------------------------------------------------------
# 実行。distroless の cc (glibc・libssl・CA 証明書を含む。ECDH と AES-GCM は OpenSSL を使う)。
# シェルも apt も無い。root では動かさない
# ---------------------------------------------------------------------------
FROM gcr.io/distroless/cc-debian13:nonroot
COPY --from=build /out/denpa-hub /app/denpa-hub
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER nonroot
ENTRYPOINT ["/app/denpa-hub"]
