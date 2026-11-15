# Vivian с настраиваемым числом кодовых групп: тот же qwen3-tts.cpp, что в deploy/vivian, плюс патч предсказателя кодов.
# QWEN_CB_GROUPS=N (2..16) говорит предсказателю считать только N групп на кадр (по умолчанию 16, как в оригинале).
# Без дообучения модели речь на меньшем числе групп выходит длиннее (на 12 примерно на 20%).

# ==== build stage ====
FROM ubuntu:22.04 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential libopenblas-dev git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY qwen_tts_code_predictor.c /ctx/qwen_tts_code_predictor.c
ARG QWEN3_TTS_REF=ef339be
RUN git clone https://github.com/gabriele-mastrapasqua/qwen3-tts.git /src \
    && cd /src \
    && git checkout "$QWEN3_TTS_REF" \
    && cp /ctx/qwen_tts_code_predictor.c /src/ \
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
