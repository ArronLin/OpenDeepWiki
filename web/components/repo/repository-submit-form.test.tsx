import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RepositorySubmitForm } from "./repository-submit-form";
import { importRepositoryDocuments, submitArchiveRepository, submitRepository } from "@/lib/repository-api";

vi.mock("@/hooks/use-translations", () => ({ useTranslations: () => (key: string) => key }));
vi.mock("@/lib/repository-api", () => ({
  importRepositoryDocuments: vi.fn(), submitRepository: vi.fn(), submitArchiveRepository: vi.fn(),
  submitLocalDirectoryRepository: vi.fn(), fetchGitBranches: vi.fn(), checkGitHubRepo: vi.fn(),
}));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal("ResizeObserver", class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
});

function fillImportForm() {
  render(<RepositorySubmitForm />);
  fireEvent.click(screen.getByRole("button", { name: /home.repository.sourceTypeDocumentation / }));
  fireEvent.change(screen.getByPlaceholderText("home.repository.orgNamePlaceholder"), { target: { value: "local" } });
  fireEvent.change(screen.getByPlaceholderText("home.repository.repoNamePlaceholder"), { target: { value: "imported" } });
  fireEvent.change(screen.getByLabelText("home.repository.archiveFile"), {
    target: { files: [new File(["zip"], "docs.zip", { type: "application/zip" })] },
  });
}

describe("generated documentation import", () => {
  it("submits directly to document import, hides generation inputs, and defaults to private", async () => {
    vi.mocked(importRepositoryDocuments).mockResolvedValue({} as never);
    fillImportForm();
    expect(screen.queryByText("home.repository.generateSkill")).toBeNull();
    expect(screen.queryByText("home.repository.language")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "home.repository.importDocuments" }));
    await waitFor(() => expect(importRepositoryDocuments).toHaveBeenCalledOnce());
    expect(importRepositoryDocuments).toHaveBeenCalledWith(expect.objectContaining({ orgName: "local", repoName: "imported", isPublic: false }));
    expect(submitRepository).not.toHaveBeenCalled();
    expect(submitArchiveRepository).not.toHaveBeenCalled();
  });

  it("keeps the form populated and announces a server conflict beside the upload", async () => {
    vi.mocked(importRepositoryDocuments).mockRejectedValue(new Error("知识库已存在，请更换名称"));
    fillImportForm();
    fireEvent.click(screen.getByRole("button", { name: "home.repository.importDocuments" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("知识库已存在，请更换名称"));
    expect(screen.getByPlaceholderText("home.repository.repoNamePlaceholder")).toHaveValue("imported");
  });
});
