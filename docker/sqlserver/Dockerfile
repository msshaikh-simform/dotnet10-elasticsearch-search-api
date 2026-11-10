# SQL Server 2022 with Full-Text Search.
#
# The stock mcr.microsoft.com/mssql/server:2022-latest image does NOT include
# full-text search - SERVERPROPERTY('IsFullTextInstalled') returns 0 - so the
# FREETEXT and CONTAINS baselines this project measures cannot run on it.
#
# Build time is a few minutes the first time; after that Docker caches the layer.
FROM mcr.microsoft.com/mssql/server:2022-latest

USER root

# Optional escape hatch for networks that intercept TLS (corporate proxies such
# as Zscaler). Without the proxy's root certificate the container cannot verify
# packages.microsoft.com and apt fails. Drop the certificate into ./certs/ and:
#   docker compose build --build-arg EXTRA_CA_CERT=your-ca.crt
# On an ordinary network you need none of this - leave it alone.
ARG EXTRA_CA_CERT=none
COPY certs/ /tmp/certs/

RUN if [ "$EXTRA_CA_CERT" != "none" ] && [ -f "/tmp/certs/$EXTRA_CA_CERT" ]; then \
        cp "/tmp/certs/$EXTRA_CA_CERT" /usr/local/share/ca-certificates/extra-ca.crt && \
        update-ca-certificates; \
    fi && \
    # The server packages live in their own repository; the base image only
    # configures 'prod', which carries the tools but not mssql-server-fts.
    echo "deb [arch=amd64] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022 jammy main" \
        > /etc/apt/sources.list.d/mssql-server-2022.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends mssql-server-fts && \
    rm -rf /var/lib/apt/lists/* /tmp/certs

USER mssql
