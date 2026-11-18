import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { NextIntlClientProvider } from "next-intl";
import home from "@/i18n/messages/en/home.json";
import type { ProcessingLogResponse, RepositoryStatus } from "@/types/repository";
import { fetchProcessingLogs, restartRepository } from "@/lib/repository-api";
import { RepositoryProcessingStatus } from "./repository-processing-status";

vi.mock("@/hooks/use-translations", () => ({ useTranslations: () => (key: string) => key }));
vi.mock("@/lib/repository-api", () => ({
  fetchProcessingLogs: vi.fn(), fetchRepoStatus: vi.fn().mockResolvedValue({ defaultSlug: "" }),
  pauseRepository: vi.fn(), resumeRepository: vi.fn(), restartRepository: vi.fn(), stopRepository: vi.fn(),
}));

function logs(statusName: RepositoryStatus): ProcessingLogResponse {
  return {
    statusName, status: 0, currentStep: 0, currentStepName: "Workspace",
    totalDocuments: 0, completedDocuments: 0, startedAt: null, logs: [],
  };
}

function show(status: RepositoryStatus) {
  render(
    <NextIntlClientProvider locale="en" timeZone="UTC" messages={{ home }}>
      <RepositoryProcessingStatus owner="test" repo="wiki" status={status} canControl />
    </NextIntlClientProvider>,
  );
  return screen.getByRole("button", { name: "Restart" });
}

describe("repository restart", () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it.each(["Pending", "Processing"] as const)("disables restart after loading a %s repository", async (status) => {
    vi.mocked(fetchProcessingLogs).mockResolvedValue(logs(status));
    const restart = show("Stopped");
    await screen.findByRole("button", { name: "Pause" });
    const label = status === "Pending" ? "Queued for generation" : "Generating docs";
    expect(restart).toHaveAccessibleName(label);
    expect(restart).toHaveAttribute("aria-busy", "true");
    expect(restart.querySelector("svg")).toHaveClass("motion-safe:animate-spin");
    expect(screen.getByRole("status")).toHaveTextContent(label);
    expect(restart).toBeDisabled();
    expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
    fireEvent.click(restart);
    expect(restartRepository).not.toHaveBeenCalled();
  });

  it("blocks repeats during submission and processing, then enables restart after completion", async () => {
    vi.mocked(fetchProcessingLogs).mockResolvedValue(logs("Stopped"));
    const restart = show("Stopped");
    await waitFor(() => expect(restart).toBeEnabled());

    let accept!: () => void;
    vi.mocked(restartRepository).mockImplementation(() => new Promise(resolve => {
      accept = () => resolve({ success: true });
    }));
    fireEvent.click(restart);
    expect(restart).toHaveAccessibleName("Submitting...");
    expect(restart).toBeDisabled();
    fireEvent.click(restart);
    vi.mocked(fetchProcessingLogs).mockResolvedValue(logs("Processing"));
    await act(async () => { accept(); });
    expect(screen.getByRole("button", { name: "Pause" })).toBeEnabled();
    expect(restart).toHaveAccessibleName("Generating docs");
    expect(restart).toBeDisabled();
    expect(restartRepository).toHaveBeenCalledTimes(1);

    vi.mocked(fetchProcessingLogs).mockResolvedValue(logs("Completed"));
    fireEvent.click(screen.getByRole("banner").querySelector("button")!);
    await waitFor(() => expect(restart).toBeEnabled());
    expect(restart).toHaveAccessibleName("Restart");
    expect(restart).not.toHaveAttribute("aria-busy");
    expect(screen.getByRole("status")).toBeEmptyDOMElement();
  });
});
