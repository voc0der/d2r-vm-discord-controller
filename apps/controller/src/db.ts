import { mkdirSync } from "node:fs";
import { dirname } from "node:path";
import { DatabaseSync } from "node:sqlite";

import type { AgentKind } from "./protocol.js";

export interface ActiveGame {
  name: string;
  password?: string;
  difficulty?: string;
  notes?: string;
  updatedBy: string;
  updatedUtc: string;
}

export class AppDb {
  private readonly db: DatabaseSync;

  public constructor(path = process.env.DB_PATH ?? "./data/controller.sqlite") {
    mkdirSync(dirname(path), { recursive: true });
    this.db = new DatabaseSync(path);
    this.db.exec(`
      create table if not exists agent_status (
        agent_id text primary key,
        kind text not null,
        connected integer not null,
        last_seen_utc text not null,
        payload_json text not null
      );

      create table if not exists command_history (
        id integer primary key autoincrement,
        command_id text not null,
        agent_id text not null,
        command text not null,
        ok integer not null,
        message text not null,
        created_utc text not null
      );

      create table if not exists active_game (
        id text primary key,
        name text not null,
        password text,
        difficulty text,
        notes text,
        updated_by text not null,
        updated_utc text not null
      );
    `);
  }

  public upsertAgentStatus(
    agentId: string,
    kind: AgentKind,
    connected: boolean,
    payload: Record<string, unknown>
  ): void {
    this.db
      .prepare(
        `
          insert into agent_status (agent_id, kind, connected, last_seen_utc, payload_json)
          values (?, ?, ?, ?, ?)
          on conflict(agent_id) do update set
            kind = excluded.kind,
            connected = excluded.connected,
            last_seen_utc = excluded.last_seen_utc,
            payload_json = excluded.payload_json
        `
      )
      .run(agentId, kind, connected ? 1 : 0, new Date().toISOString(), JSON.stringify(payload));
  }

  public insertCommandHistory(
    commandId: string,
    agentId: string,
    command: string,
    ok: boolean,
    message: string
  ): void {
    this.db
      .prepare(
        `
          insert into command_history (command_id, agent_id, command, ok, message, created_utc)
          values (?, ?, ?, ?, ?, ?)
        `
      )
      .run(commandId, agentId, command, ok ? 1 : 0, message, new Date().toISOString());
  }

  public setActiveGame(input: {
    name: string;
    password?: string;
    difficulty?: string;
    notes?: string;
    updatedBy: string;
  }): ActiveGame {
    const updatedUtc = new Date().toISOString();
    this.db
      .prepare(
        `
          insert into active_game (id, name, password, difficulty, notes, updated_by, updated_utc)
          values ('current', ?, ?, ?, ?, ?, ?)
          on conflict(id) do update set
            name = excluded.name,
            password = excluded.password,
            difficulty = excluded.difficulty,
            notes = excluded.notes,
            updated_by = excluded.updated_by,
            updated_utc = excluded.updated_utc
        `
      )
      .run(
        input.name,
        input.password ?? null,
        input.difficulty ?? null,
        input.notes ?? null,
        input.updatedBy,
        updatedUtc
      );

    return {
      name: input.name,
      password: input.password,
      difficulty: input.difficulty,
      notes: input.notes,
      updatedBy: input.updatedBy,
      updatedUtc
    };
  }

  public getActiveGame(): ActiveGame | undefined {
    const row = this.db
      .prepare(
        `
          select name, password, difficulty, notes, updated_by as updatedBy, updated_utc as updatedUtc
          from active_game
          where id = 'current'
        `
      )
      .get() as
      | {
          name: string;
          password: string | null;
          difficulty: string | null;
          notes: string | null;
          updatedBy: string;
          updatedUtc: string;
        }
      | undefined;

    if (!row) {
      return undefined;
    }

    return {
      name: row.name,
      password: row.password ?? undefined,
      difficulty: row.difficulty ?? undefined,
      notes: row.notes ?? undefined,
      updatedBy: row.updatedBy,
      updatedUtc: row.updatedUtc
    };
  }

  public clearActiveGame(): boolean {
    const result = this.db.prepare("delete from active_game where id = 'current'").run();
    return result.changes > 0;
  }
}
