# The Propane multiplayer server: the exported Linux game, run headless in server mode.
# The build (Propane.x86_64, Propane.pck and the data_ folder) is copied into ./game by deploy.sh.
FROM debian:trixie-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libicu76 libfontconfig1 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --create-home --uid 1000 propane
COPY game /opt/propane
RUN chmod +x /opt/propane/Propane.x86_64
USER propane
WORKDIR /opt/propane
EXPOSE 24680/udp
ENTRYPOINT ["/opt/propane/Propane.x86_64", "--headless", "--"]
CMD ["--server", "--port=24680"]
