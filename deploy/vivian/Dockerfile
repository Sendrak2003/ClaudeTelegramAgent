# Сервер озвучки голосом Vivian: qwen3-tts.cpp (MIT) + модель Qwen3-TTS 0.6B CustomVoice (Apache-2.0).
# Модель (около 650 МБ) скачивается при первом запуске в том /models и дальше хранится там.

# ==== build stage ====
FROM ubuntu:22.04 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential libopenblas-dev git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Фиксируем версию движка, с которой проверяли скорость и качество (на сервере ставился ef339be).
ARG QWEN3_TTS_REF=ef339be
RUN git clone https://github.com/gabriele-mastrapasqua/qwen3-tts.git /src \
    && cd /src \
    && git checkout "$QWEN3_TTS_REF" \
    && make blas SIMD=portable

# ==== runtime stage ====
FROM ubuntu:22.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends libopenblas0 libgomp1 curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /src /app
COPY entrypoint.sh /app/vivian-entrypoint.sh
RUN chmod +x /app/vivian-entrypoint.sh /app/download_model.sh

WORKDIR /app
EXPOSE 8099
ENTRYPOINT ["/app/vivian-entrypoint.sh"]
