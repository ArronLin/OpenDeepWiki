import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RepositorySubmitForm } from "./repository-submit-form";
import { importRepositoryDocuments, submitArchiveRepository, submitLocalDirectoryRepository, submitRepository } from "@/lib/repository-api";
import { api } from "@/lib/api-client";

vi.mock("@/hooks/use-translations", () => ({ useTranslations: () => (key: string) => key }));
vi.mock("@/lib/repository-api", () => ({
  importRepositoryDocuments: vi.fn(), submitRepository: vi.fn(), submitArchiveRepository: vi.fn(),
  submitLocalDirectoryRepository: vi.fn(), fetchGitBranches: vi.fn(), checkGitHubRepo: vi.fn(),
}));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock("@/lib/api-client", async (importOriginal) => ({
  ...await importOriginal<typeof import("@/lib/api-client")>(),
  api: { get: vi.fn(), put: vi.fn() },
}));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.get).mockResolvedValue({ roots: [], canEdit: false });
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

describe("local directory import", () => {
  function fillLocalForm() {
    render(<RepositorySubmitForm />);
    fireEvent.click(screen.getByRole("button", { name: /home.repository.sourceTypeLocal / }));
    fireEvent.change(screen.getByPlaceholderText("home.repository.localPathPlaceholder"), { target: { value: "D:\\Repos\\SampleRepo" } });
    fireEvent.change(screen.getByPlaceholderText("home.repository.orgNamePlaceholder"), { target: { value: "example" } });
    fireEvent.change(screen.getByPlaceholderText("home.repository.repoNamePlaceholder"), { target: { value: "SampleRepo" } });
  }

  it("sends the original host path for server-side mapping without Git credentials", async () => {
    vi.mocked(submitLocalDirectoryRepository).mockResolvedValue({} as never);
    fillLocalForm();
    fireEvent.click(screen.getByRole("button", { name: "home.repository.submit" }));
    await waitFor(() => expect(submitLocalDirectoryRepository).toHaveBeenCalledWith(expect.objectContaining({
      localPath: "D:\\Repos\\SampleRepo", isPublic: false,
    })));
    expect(submitRepository).not.toHaveBeenCalled();
  });

  it("shows the server error beside the path and preserves the form for retry", async () => {
    vi.mocked(submitLocalDirectoryRepository).mockRejectedValue(new Error("找不到该目录，请检查挂载映射"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    fillLocalForm();
    fireEvent.click(screen.getByRole("button", { name: "home.repository.submit" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("找不到该目录，请检查挂载映射"));
    expect(screen.getByPlaceholderText("home.repository.localPathPlaceholder")).toHaveValue("D:\\Repos\\SampleRepo");
    vi.mocked(console.error).mockRestore();
  });

  it("browses the allowed roots and fills the form with the selected host path", async () => {
    vi.mocked(api.get)
      .mockResolvedValueOnce({ roots: ["D:/Repos"], canEdit: false })
      .mockResolvedValueOnce({ path: "D:/Repos", folders: [{ name: "SampleRepo", path: "D:/Repos/SampleRepo" }] })
      .mockResolvedValueOnce({ path: "D:/Repos/SampleRepo", folders: [] });
    fillLocalForm();
    fireEvent.change(screen.getByPlaceholderText("home.repository.localPathPlaceholder"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "home.repository.browseLocal" }));
    const root = await screen.findByRole("button", { name: "D:/Repos" });
    if (!screen.queryByRole("button", { name: "SampleRepo" })) fireEvent.click(root);
    fireEvent.doubleClick(await screen.findByRole("button", { name: "SampleRepo" }));
    fireEvent.click(await screen.findByRole("button", { name: "home.repository.chooseLocalFolder" }));
    expect(screen.getByPlaceholderText("home.repository.localPathPlaceholder")).toHaveValue("D:/Repos/SampleRepo");
    expect(screen.queryByRole("button", { name: "home.repository.editLocalRoots" })).toBeNull();
  });

  it("lets admins save an edited allowlist without submitting the repository form", async () => {
    vi.mocked(api.get).mockResolvedValueOnce({ roots: ["D:/Repos"], canEdit: true });
    vi.mocked(api.put).mockResolvedValueOnce({ roots: ["D:/Repos/SampleRepo"], canEdit: true });
    fillLocalForm();
    fireEvent.click(await screen.findByRole("button", { name: "home.repository.editLocalRoots" }));
    fireEvent.change(screen.getByRole("textbox", { name: "home.repository.localRoots" }), {
      target: { value: "D:/Repos/SampleRepo" },
    });
    fireEvent.click(screen.getByRole("button", { name: "home.repository.saveLocalRoots" }));
    expect(api.put).toHaveBeenCalledWith("/api/v1/repositories/local-directories/roots", ["D:/Repos/SampleRepo"]);
    await waitFor(() => expect(screen.getByText("D:/Repos/SampleRepo", { selector: "li" })).toBeInTheDocument());
    expect(submitLocalDirectoryRepository).not.toHaveBeenCalled();
  });
});
