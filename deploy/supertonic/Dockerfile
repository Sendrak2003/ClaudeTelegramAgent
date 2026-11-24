# Supertonic 3 (ONNX, CPU): код MIT, модель OpenRAIL-M (условия на huggingface.co/supertone-oss-archive/supertonic-3).
# Проект заархивирован автором: версии закреплены, чтобы сборка не менялась.
FROM python:3.11-slim

RUN pip install --no-cache-dir onnxruntime==1.23.1 numpy scipy num2words huggingface_hub

WORKDIR /app

# helper.py из архивного репозитория (закреплённый коммит) и модель (закреплённая ревизия).
ARG SUPERTONIC_REF=1e9799e964ea4c0dad7cde993b65c3c813a7b373
ARG MODEL_REV=aafc6e32416a594460b32413efc49d7fe4ce6d46
RUN python -c "import urllib.request; urllib.request.urlretrieve('https://raw.githubusercontent.com/supertone-oss-archive/supertonic/${SUPERTONIC_REF}/py/helper.py', '/app/helper.py')" \
    && python -c "from huggingface_hub import snapshot_download; snapshot_download('supertone-oss-archive/supertonic-3', revision='${MODEL_REV}', local_dir='/app/assets', allow_patterns=['onnx/*','voice_styles/*','config.json','LICENSE'])"

COPY server.py .

EXPOSE 8099
CMD ["python", "-u", "server.py"]
