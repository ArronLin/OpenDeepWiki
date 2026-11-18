import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import admin from "@/i18n/messages/en/admin.json";
import { ScanPlanEvaluationStatus, ScanPlanSummary } from "./scan-plan-summary";
import type { ComponentProps } from "react";

const plan: ComponentProps<typeof ScanPlanSummary>["plan"] = {
  id: "plan-1",
  source: "AI",
  model: "planning-model",
  commit: "1234567890abcdef",
  evaluatedAt: "2026-11-18T00:00:00Z",
  planningTokens: 1_200,
  excluded: { ignored: 2 },
  modules: [
    {
      root: "src", title: "Application", files: ["src/a.ts", "src/b.ts", "src/c.ts"],
      requiredFiles: ["src/a.ts", "src/b.ts"], questions: ["Where do requests enter?"],
      dependencies: [], topics: [],
    },
    {
      root: "tests", title: "Tests", files: ["tests/a.ts"],
      requiredFiles: ["tests/a.ts"], questions: [], dependencies: [], topics: [],
    },
  ],
};

describe("ScanPlanSummary", () => {
  it("shows persisted evaluation errors even before a branch has a plan", () => {
    const notice = (status: string) => (
      <NextIntlClientProvider locale="en" timeZone="UTC" messages={{ admin }}>
        <ScanPlanEvaluationStatus branch="feature" evaluation={{
          taskId: "task", status, errorMessage: status === "Failed" ? "File outside module" : undefined,
        }} />
      </NextIntlClientProvider>
    );
    const { rerender } = render(notice("Pending"));
    expect(screen.getByRole("status")).toHaveTextContent("Queued");
    rerender(notice("Processing"));
    expect(screen.getByRole("status")).toHaveTextContent("Running");
    rerender(notice("Failed"));
    expect(screen.getByRole("alert")).toHaveTextContent("Latest re-evaluation for feature · Evaluation failed");
    expect(screen.getByRole("alert")).toHaveTextContent("File outside module");
    rerender(notice("Completed"));
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("shows the current plan totals and reveals file paths on demand", () => {
    render(
      <NextIntlClientProvider locale="en" timeZone="UTC" messages={{ admin }}>
        <ScanPlanSummary branch="main" plan={plan} />
      </NextIntlClientProvider>
    );

    expect(screen.getByText("Inventoried files").parentElement).toHaveTextContent("4");
    expect(screen.getByText("Files prioritized for reading").parentElement).toHaveTextContent("3");
    expect(screen.getByText(/most concentrated in src \(2 files, 67%\)/)).toBeInTheDocument();

    const details = screen.getByRole("button", { name: "View module details" });
    expect(details).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("src/a.ts")).not.toBeInTheDocument();
    fireEvent.click(details);
    expect(details).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Where do requests enter?")).toBeInTheDocument();
    expect(screen.getByText("src/a.ts")).toBeInTheDocument();
  });

  it("counts distinct read and cited files and displays only matching generation records", () => {
    const run = {
      planId: plan.id,
      commit: plan.commit,
      startedAt: "2026-11-18T00:00:00Z",
      finishedAt: "2026-11-18T00:01:00Z",
      batches: [
        { module: "src", page: "src/1", reads: [{ path: "src/a.ts" }], citedFiles: ["src/a.ts"], gaps: ["Test coverage"], tokens: 10 },
        { module: "src", page: "src/2", reads: [{ path: "src/a.ts" }, { path: "src/b.ts" }], citedFiles: ["src/a.ts"], gaps: ["Call chain"], tokens: 20 },
      ],
    };
    render(
      <NextIntlClientProvider locale="en" timeZone="UTC" messages={{ admin }}>
        <ScanPlanSummary branch="main" plan={plan} progress={[
          { language: "en", progress: run },
          { language: "zh", progress: { ...run, planId: "old-plan" } },
        ]} />
      </NextIntlClientProvider>
    );

    expect(screen.getByText("Files read 2 · Cited in docs 1 · Modules with known gaps 1")).toBeInTheDocument();
    expect(screen.getByText("Partially complete")).toBeInTheDocument();
    expect(screen.queryByText("Generation results (zh)")).not.toBeInTheDocument();
  });
});
