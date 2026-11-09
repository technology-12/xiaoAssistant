FROM node:24-bookworm-slim
WORKDIR /app
COPY gateway/package.json gateway/package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund && mkdir -p /data && chown node:node /data
COPY --chown=node:node gateway/src ./src
COPY --chown=node:node gateway/LICENSES ./LICENSES
ENV XIAO_HOST=0.0.0.0 XIAO_PORT=4388 XIAO_DATA_ROOT=/data
USER node
EXPOSE 4388
CMD ["node", "src/server.mjs"]
