export type AgentKind = "vm" | "host";

export type ControllerCommand =
  | "launch_battlenet"
  | "launch_d2r"
  | "kill_d2r"
  | "restart_d2r"
  | "screenshot"
  | "status"
  | "menu_ready"
  | "menu_lobby"
  | "menu_play"
  | "menu_join_game"
  | "menu_create_game"
  | "menu_join_friend"
  | "menu_save_exit"
  | "vm_status"
  | "vm_start"
  | "vm_stop"
  | "vm_reboot"
  | "vm_snapshot";

export interface AgentHelloMessage {
  type: "hello";
  agentId: string;
  agentKind: AgentKind;
  sharedSecret: string;
  version?: string;
  hostName?: string;
}

export interface AgentStatusMessage {
  type: "status";
  agentId: string;
  status: Record<string, unknown>;
}

export interface CommandResultMessage {
  type: "command_result";
  agentId: string;
  commandId: string;
  ok: boolean;
  message: string;
  data?: unknown;
}

export interface ControllerCommandMessage {
  type: "command";
  commandId: string;
  command: ControllerCommand;
  args?: Record<string, unknown>;
}

export type AgentToControllerMessage =
  | AgentHelloMessage
  | AgentStatusMessage
  | CommandResultMessage;

export interface ScreenshotResultData {
  mimeType: string;
  base64: string;
}

export function isScreenshotResultData(value: unknown): value is ScreenshotResultData {
  if (!value || typeof value !== "object") {
    return false;
  }

  const maybe = value as Partial<ScreenshotResultData>;
  return typeof maybe.mimeType === "string" && typeof maybe.base64 === "string";
}
