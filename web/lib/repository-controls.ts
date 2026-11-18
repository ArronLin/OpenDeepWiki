import type { RepositoryStatus } from "@/types/repository";

export type ControlAction = "pause" | "resume" | "stop" | "restart";

export const visibleControlActions: Record<RepositoryStatus, ControlAction[]> = {
  Pending: ["pause", "stop", "restart"],
  Processing: ["pause", "stop", "restart"],
  Paused: ["resume", "stop", "restart"],
  Stopped: ["restart"],
  Failed: ["restart"],
  Completed: ["restart"],
};

export function canRestartRepository(status: RepositoryStatus | null) {
  return status === "Completed" || status === "Failed" || status === "Stopped" || status === "Paused";
}
