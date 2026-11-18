import { useLocale, useTranslations } from "next-intl";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";
import { Badge } from "@/components/ui/badge";
import type { AdminRepositoryScanPlan } from "@/lib/admin-api";

interface ScanPlanSummaryProps {
  branch: string;
  plan: NonNullable<AdminRepositoryScanPlan["deepPlan"]>;
  progress?: AdminRepositoryScanPlan["deepProgress"];
  changes?: string;
}

export function ScanPlanEvaluationStatus({ branch, evaluation }: {
  branch: string;
  evaluation: NonNullable<AdminRepositoryScanPlan["latestEvaluation"]>;
}) {
  const t = useTranslations("admin.repositories.management.scanPlan");
  const failed = evaluation.status === "Failed";
  return (
    <div role={failed ? "alert" : "status"} className={failed
      ? "space-y-1 rounded-lg border border-destructive/40 bg-destructive/5 p-3 text-sm"
      : "rounded-lg border bg-muted/30 p-3 text-sm"}>
      <p className="font-medium">
        {t("lastEvaluation", { branch })} · {t(`evaluationStatus.${evaluation.status}`)}
      </p>
      {failed && evaluation.errorMessage && <p className="break-words">{evaluation.errorMessage}</p>}
    </div>
  );
}

export function ScanPlanSummary({ branch, plan, progress, changes }: ScanPlanSummaryProps) {
  const t = useTranslations("admin.repositories.management.scanPlan");
  const locale = useLocale();
  const numbers = new Intl.NumberFormat(locale);
  const percentages = new Intl.NumberFormat(locale, { style: "percent" });
  const fileCount = plan.modules.reduce((total, module) => total + module.files.length, 0);
  const requiredCount = plan.modules.reduce((total, module) => total + module.requiredFiles.length, 0);
  const mostRead = plan.modules.reduce<(typeof plan.modules)[number] | undefined>(
    (largest, module) => !largest || module.requiredFiles.length > largest.requiredFiles.length ? module : largest,
    undefined
  );
  const progressByLanguage = (progress ?? []).flatMap(({ language, progress: run }) => {
    if (!run || run.planId !== plan.id) return [];
    const gaps = run.batches.filter((batch) => batch.gaps.length > 0);
    return [{
      language,
      run,
      gaps,
      gapModules: new Set(gaps.map((batch) => batch.module)).size,
      readFiles: new Set(run.batches.flatMap((batch) => batch.reads.map((read) => read.path))).size,
      citedFiles: new Set(run.batches.flatMap((batch) => batch.citedFiles ?? [])).size,
      tokens: run.batches.reduce((total, batch) => total + batch.tokens, 0),
    }];
  });

  return (
    <div className="min-w-0 space-y-4 rounded-lg border bg-muted/10 p-4">
      <header className="space-y-1">
        <h3 className="text-sm font-semibold">{t("summaryTitle")}</h3>
        <p className="break-words text-xs text-muted-foreground">
          {t("effectiveBranch", { branch })} · {t("planSources." + (plan.source === "AI" || plan.source === "Mixed" ? plan.source : "Rules"))}
          {plan.model && <> · {plan.model}</>}
          {plan.commit && <> · <span className="font-mono" title={plan.commit}>{plan.commit.slice(0, 10)}</span></>}
        </p>
        {plan.reason && <p className="text-sm text-muted-foreground">{plan.reason}</p>}
      </header>

      <dl className="grid gap-2 sm:grid-cols-3">
        {([
          ["moduleCount", plan.modules.length],
          ["inventoriedFiles", fileCount],
          ["plannedReads", requiredCount],
        ] as const).map(([label, count]) => (
          <div key={label} className="flex items-center justify-between gap-2 rounded-md border bg-background p-3 sm:block">
            <dt className="text-sm text-muted-foreground">{t(label)}</dt>
            <dd className="text-xl font-semibold tabular-nums">{numbers.format(count)}</dd>
          </div>
        ))}
      </dl>

      <div className="space-y-1 text-sm">
        {mostRead && requiredCount > 0 && plan.modules.length > 1 && (
          <p className="break-words">
            {t("readingFocus", {
              module: mostRead.root === "." ? t("rootDirectory") : mostRead.root,
              count: numbers.format(mostRead.requiredFiles.length),
              share: percentages.format(mostRead.requiredFiles.length / requiredCount),
            })}
          </p>
        )}
        <p className="text-muted-foreground">{t("analysisScope")}</p>
      </div>

      {progressByLanguage.map(({ language, run, readFiles, citedFiles, gapModules }) => (
        <div key={language} className="space-y-1 border-t pt-3 text-sm">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium">{t("generationRecord", { language })}</p>
            {run.finishedAt && gapModules > 0 && <Badge variant="secondary">{t("partial")}</Badge>}
          </div>
          <p className="text-muted-foreground">
            {t("coverage", {
              read: numbers.format(readFiles),
              cited: numbers.format(citedFiles),
              gaps: numbers.format(gapModules),
            })}
          </p>
        </div>
      ))}

      <Accordion type="single" collapsible className="border-t">
        <AccordionItem value="modules">
          <AccordionTrigger className="min-h-11 py-3">{t("moduleDetails")}</AccordionTrigger>
          <AccordionContent className="space-y-3 pb-0">
            <p className="text-muted-foreground">{t("planningUsage", { tokens: numbers.format(plan.planningTokens) })}</p>
            {changes && <p className="break-words">{changes}</p>}
            {Object.keys(plan.excluded).length > 0 && (
              <p className="break-words text-muted-foreground">
                {t("excludedEntries")} · {Object.entries(plan.excluded).map(([reason, count]) => `${reason} ${numbers.format(count)}`).join(" · ")}
              </p>
            )}
            {plan.modules.map((module) => (
              <details key={module.root} className="rounded-md border bg-background px-3">
                <summary className="min-h-11 cursor-pointer break-words py-3 focus-visible:outline-2 focus-visible:outline-ring">
                  {module.title} · {module.root} · {t("moduleReads", {
                    required: numbers.format(module.requiredFiles.length),
                    files: numbers.format(module.files.length),
                  })}
                </summary>
                <div className="space-y-2 break-words pb-3">
                  <p>{module.questions.join(" · ")}</p>
                  <p>{module.dependencies.concat(module.topics).join(" · ")}</p>
                  <ul className="max-h-36 space-y-1 overflow-auto">
                    {module.requiredFiles.map((file) => <li className="break-all" key={file}>{file}</li>)}
                  </ul>
                </div>
              </details>
            ))}
            {progressByLanguage.map(({ language, run, gaps, tokens }) => (
              <div key={language} className="space-y-1 border-t pt-2 text-muted-foreground">
                <p>{t("generationUsage", {
                  language, batches: numbers.format(run.batches.length), tokens: numbers.format(tokens),
                })}</p>
                {run.finishedAt && <p>{t("elapsedSeconds", {
                  seconds: numbers.format(Math.round((Date.parse(run.finishedAt) - Date.parse(run.startedAt)) / 1000)),
                })}</p>}
                {gaps.map((batch) => <p key={batch.page} className="break-words">{batch.module} · {batch.gaps.join(" / ")}</p>)}
              </div>
            ))}
          </AccordionContent>
        </AccordionItem>
      </Accordion>
    </div>
  );
}
