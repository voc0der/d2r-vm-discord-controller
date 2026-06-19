import { randomUUID } from "node:crypto";
import type { IncomingMessage } from "node:http";
import WebSocket from "ws";

import type { ControllerConfig } from "./config.js";
import type { AppDb } from "./db.js";
import type {
  AgentHelloMessage,
  AgentKind,
  AgentStatusMessage,
  AgentToControllerMessage,
  CommandResultMessage,
  ControllerCommand,
  ControllerCommandMessage
} from "./protocol.js";
import { logger } from "./logger.js";

interface ConnectedAgent {
  id: string;
  kind: AgentKind;
  displayName?: string;
  hostName?: string;
  version?: string;
  connectedAt: Date;
  lastSeenAt: Date;
  lastStatus?: Record<string, unknown>;
  socket: WebSocket;
}

interface PendingCommand {
  agentId: string;
  command: ControllerCommand;
  resolve: (value: CommandResultMessage) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

export interface AgentSnapshot {
  id: string;
  kind: AgentKind;
  displayName?: string;
  hostName?: string;
  version?: string;
  connected: boolean;
  connectedAt?: string;
  lastSeenAt?: string;
  lastStatus?: Record<string, unknown>;
}

export class AgentRegistry {
  private readonly agents = new Map<string, ConnectedAgent>();
  private readonly pending = new Map<string, PendingCommand>();

  public constructor(
    private readonly config: ControllerConfig,
    private readonly db: AppDb
  ) {}

  public handleConnection(socket: WebSocket, request: IncomingMessage): void {
    let agentId: string | undefined;
    const remoteAddress = request.socket.remoteAddress;
    logger.info({ remoteAddress }, "agent websocket connected");

    const unauthenticatedTimer = setTimeout(() => {
      if (!agentId) {
        socket.close(1008, "hello required");
      }
    }, 5000);

    socket.on("message", (data) => {
      let message: AgentToControllerMessage;
      try {
        message = JSON.parse(data.toString()) as AgentToControllerMessage;
      } catch {
        socket.close(1003, "invalid json");
        return;
      }

      if (!agentId) {
        if (message.type !== "hello") {
          socket.close(1008, "first message must be hello");
          return;
        }

        const hello = message as AgentHelloMessage;
        const configured = this.config.agents[hello.agentId];
        if (!configured || configured.sharedSecret !== hello.sharedSecret || configured.kind !== hello.agentKind) {
          logger.warn({ agentId: hello.agentId, remoteAddress }, "agent authentication failed");
          socket.close(1008, "authentication failed");
          return;
        }

        clearTimeout(unauthenticatedTimer);
        agentId = hello.agentId;
        this.registerAgent(socket, hello);
        return;
      }

      if (message.type === "status") {
        this.updateStatus(agentId, message as AgentStatusMessage);
        return;
      }

      if (message.type === "command_result") {
        this.completeCommand(message as CommandResultMessage);
        return;
      }
    });

    socket.on("close", () => {
      clearTimeout(unauthenticatedTimer);
      if (!agentId) {
        return;
      }

      const agent = this.agents.get(agentId);
      if (agent?.socket === socket) {
        this.agents.delete(agentId);
        this.db.upsertAgentStatus(agent.id, agent.kind, false, agent.lastStatus ?? {});
        logger.warn({ agentId }, "agent disconnected");
      }
    });

    socket.on("error", (error) => {
      logger.warn({ error, agentId }, "agent websocket error");
    });
  }

  public snapshot(): AgentSnapshot[] {
    const configured = Object.entries(this.config.agents).map(([id, agentConfig]) => {
      const connected = this.agents.get(id);
      if (connected) {
        return this.toSnapshot(connected, true);
      }

      return {
        id,
        kind: agentConfig.kind,
        displayName: agentConfig.displayName,
        connected: false
      };
    });

    return configured.sort((a, b) => a.id.localeCompare(b.id));
  }

  public getAgent(agentId: string): AgentSnapshot | undefined {
    const connected = this.agents.get(agentId);
    if (connected) {
      return this.toSnapshot(connected, true);
    }

    const configured = this.config.agents[agentId];
    if (!configured) {
      return undefined;
    }

    return {
      id: agentId,
      kind: configured.kind,
      displayName: configured.displayName,
      connected: false
    };
  }

  public async sendCommand(
    agentId: string,
    command: ControllerCommand,
    args: Record<string, unknown> = {},
    timeoutMs = 45_000
  ): Promise<CommandResultMessage> {
    const agent = this.agents.get(agentId);
    if (!agent || agent.socket.readyState !== WebSocket.OPEN) {
      throw new Error(`Agent "${agentId}" is not connected.`);
    }

    const commandId = randomUUID();
    const payload: ControllerCommandMessage = {
      type: "command",
      commandId,
      command,
      args
    };

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(commandId);
        reject(new Error(`Command "${command}" timed out for agent "${agentId}".`));
      }, timeoutMs);

      this.pending.set(commandId, {
        agentId,
        command,
        resolve,
        reject,
        timer
      });

      agent.socket.send(JSON.stringify(payload), (error) => {
        if (!error) {
          return;
        }

        clearTimeout(timer);
        this.pending.delete(commandId);
        reject(error);
      });
    });
  }

  private registerAgent(socket: WebSocket, hello: AgentHelloMessage): void {
    const existing = this.agents.get(hello.agentId);
    if (existing && existing.socket !== socket) {
      existing.socket.close(1000, "new connection established");
    }

    const configured = this.config.agents[hello.agentId];
    const agent: ConnectedAgent = {
      id: hello.agentId,
      kind: hello.agentKind,
      displayName: configured.displayName,
      hostName: hello.hostName,
      version: hello.version,
      connectedAt: new Date(),
      lastSeenAt: new Date(),
      socket
    };

    this.agents.set(hello.agentId, agent);
    this.db.upsertAgentStatus(agent.id, agent.kind, true, {});
    logger.info({ agentId: agent.id, kind: agent.kind, hostName: agent.hostName }, "agent authenticated");
  }

  private updateStatus(agentId: string, message: AgentStatusMessage): void {
    const agent = this.agents.get(agentId);
    if (!agent) {
      return;
    }

    agent.lastSeenAt = new Date();
    agent.lastStatus = message.status;
    this.db.upsertAgentStatus(agent.id, agent.kind, true, message.status);
  }

  private completeCommand(message: CommandResultMessage): void {
    const pending = this.pending.get(message.commandId);
    if (!pending) {
      logger.warn({ commandId: message.commandId, agentId: message.agentId }, "received unknown command result");
      return;
    }

    clearTimeout(pending.timer);
    this.pending.delete(message.commandId);
    this.db.insertCommandHistory(
      message.commandId,
      pending.agentId,
      pending.command,
      message.ok,
      message.message
    );
    pending.resolve(message);
  }

  private toSnapshot(agent: ConnectedAgent, connected: boolean): AgentSnapshot {
    return {
      id: agent.id,
      kind: agent.kind,
      displayName: agent.displayName,
      hostName: agent.hostName,
      version: agent.version,
      connected,
      connectedAt: agent.connectedAt.toISOString(),
      lastSeenAt: agent.lastSeenAt.toISOString(),
      lastStatus: agent.lastStatus
    };
  }
}
