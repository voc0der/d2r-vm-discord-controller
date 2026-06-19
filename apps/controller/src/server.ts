import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { WebSocketServer } from "ws";

import type { ControllerConfig } from "./config.js";
import type { AgentRegistry } from "./agents.js";
import { logger } from "./logger.js";

interface ServerOptions {
  config: ControllerConfig;
  registry: AgentRegistry;
  port: number;
}

export function startControllerServer({ config, registry, port }: ServerOptions): Promise<string> {
  const server = createServer((request, response) => {
    if (request.url === "/healthz") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, agents: registry.snapshot().length }));
      return;
    }

    if (request.url === "/agents") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify(registry.snapshot(), null, 2));
      return;
    }

    if (request.url === "/config/accounts") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify(Object.keys(config.accounts).sort()));
      return;
    }

    response.writeHead(404, { "content-type": "application/json" });
    response.end(JSON.stringify({ ok: false, error: "not found" }));
  });

  const wss = new WebSocketServer({ server, path: "/agent" });
  wss.on("connection", (socket, request) => registry.handleConnection(socket, request));

  return new Promise((resolve) => {
    server.listen(port, () => {
      const address = server.address() as AddressInfo;
      const url = `http://0.0.0.0:${address.port}`;
      logger.info({ url }, "controller http/ws server listening");
      resolve(url);
    });
  });
}
