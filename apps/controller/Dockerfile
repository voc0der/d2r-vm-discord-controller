FROM node:24-bookworm-slim AS deps

WORKDIR /app
COPY package.json package-lock.json ./
COPY apps/controller/package.json apps/controller/package.json
RUN npm ci

FROM deps AS build

COPY apps/controller apps/controller
RUN npm -w @d2r-ops/controller run build
RUN npm prune --omit=dev

FROM node:24-bookworm-slim AS runtime

ENV NODE_ENV=production \
    HTTP_PORT=8080 \
    CONFIG_PATH=/config/controller.config.json \
    DB_PATH=/data/controller.sqlite

WORKDIR /app
COPY --from=build /app/package.json /app/package-lock.json ./
COPY --from=build /app/apps/controller/package.json apps/controller/package.json
COPY --from=build /app/apps/controller/dist apps/controller/dist
COPY --from=build /app/node_modules node_modules

RUN mkdir -p /config /data
EXPOSE 8080

CMD ["node", "apps/controller/dist/index.js"]
