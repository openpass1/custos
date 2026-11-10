# Test server for custos-helper: OpenSSH, sudo, rsyslog and database servers, all from
# the distribution's own packages, as on a real VPS.
# Example: docker build --build-arg BASE=debian:12 --build-arg ENGINES="mariadb postgresql" helper/tests
ARG BASE=debian:12
FROM ${BASE}
ARG ENGINES=mariadb
ENV ENGINES=${ENGINES}
# python3 is only for the tests: a JSON check and a unix socket among the test files.
RUN set -eux; \
    pkgs="openssh-server sudo rsyslog tar findutils gzip python3"; \
    for e in $ENGINES; do pkgs="$pkgs $e-server"; done; \
    if command -v apt-get >/dev/null; then \
        export DEBIAN_FRONTEND=noninteractive; \
        apt-get update; \
        apt-get install -y --no-install-recommends \
            $(echo "$pkgs openssh-client procps" | sed 's/postgresql-server/postgresql/'); \
        rm -rf /var/lib/apt/lists/*; \
    else \
        dnf install -y $pkgs openssh-clients procps-ng; \
        dnf clean all; \
    fi
