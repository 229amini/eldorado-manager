# Where Docker Hub is blocked, build with a mirror:
#   docker compose build --build-arg PYTHON_IMAGE=docker.arvancloud.ir/library/python:3.12-alpine
ARG PYTHON_IMAGE=python:3.12-alpine
FROM ${PYTHON_IMAGE}

LABEL org.opencontainers.image.source="https://github.com/229amini/eldorado-manager" \
      org.opencontainers.image.description="Order alarms for Eldorado.gg sellers" \
      org.opencontainers.image.licenses="MIT"

RUN addgroup -g 10001 app && adduser -u 10001 -G app -H -D -s /sbin/nologin app \
    && mkdir /data && chown 10001:10001 /data

WORKDIR /app
COPY eldorado_manager ./eldorado_manager

ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
USER 10001:10001
VOLUME ["/data"]
EXPOSE 8787

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
    CMD wget -q -O /dev/null http://127.0.0.1:8787/healthz || exit 1

CMD ["python", "-m", "eldorado_manager", "run"]
