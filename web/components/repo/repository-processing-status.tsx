"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  Activity,
  ArrowLeft,
  Brain,
  CheckCircle2,
  Clock,
  FileCode,
  GitBranch,
  Home,
  Languages,
  Loader2,
  Network,
  Pause,
  Play,
  RefreshCw,
  Sparkles,
  Square,
  XCircle,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import {
  fetchRepoStatus,
  fetchProcessingLogs,
  pauseRepository,
  restartRepository,
  resumeRepository,
  stopRepository,
} from "@/lib/repository-api";
import { ApiError } from "@/lib/api-client";
import { buildRepoDocPath } from "@/lib/repo-route";
import { RepositoryRestartButton } from "./repository-restart-button";
import { canRestartRepository, visibleControlActions, type ControlAction } from "@/lib/repository-controls";
import type { ProcessingStep, RepositoryStatus } from "@/types/repository";

interface RepositoryProcessingStatusProps {
  owner: string;
  repo: string;
  status: RepositoryStatus;
  canControl?: boolean;
}

const controlActionIcons = {
  pause: Pause,
  resume: Play,
  stop: Square,
};

const controlActionFallbacks: Record<ControlAction, string> = {
  pause: "Pause",
  resume: "Resume",
  stop: "Stop",
  restart: "Restart",
};

const controlActionHandlers: Record<
  ControlAction,
  (owner: string, repo: string) => Promise<{ success: boolean; errorMessage?: string }>
> = {
  pause: pauseRepository,
  resume: resumeRepository,
  stop: stopRepository,
  restart: restartRepository,
};

const statusConfig: Record<
  RepositoryStatus,
  {
    icon: typeof Loader2;
    textClass: string;
    softClass: string;
    borderClass: string;
    barClass: string;
    fallback: string;
  }
> = {
  Pending: {
    icon: Clock,
    textClass: "text-amber-400",
    softClass: "bg-amber-500/10",
    borderClass: "border-amber-500/30",
    barClass: "bg-amber-400",
    fallback: "Pending",
  },
  Processing: {
    icon: Loader2,
    textClass: "text-sky-400",
    softClass: "bg-sky-500/10",
    borderClass: "border-sky-500/30",
    barClass: "bg-sky-400",
    fallback: "Processing",
  },
  Completed: {
    icon: CheckCircle2,
    textClass: "text-emerald-400",
    softClass: "bg-emerald-500/10",
    borderClass: "border-emerald-500/30",
    barClass: "bg-emerald-400",
    fallback: "Completed",
  },
  Failed: {
    icon: XCircle,
    textClass: "text-rose-400",
    softClass: "bg-rose-500/10",
    borderClass: "border-rose-500/30",
    barClass: "bg-rose-400",
    fallback: "Failed",
  },
  Paused: {
    icon: Pause,
    textClass: "text-amber-400",
    softClass: "bg-amber-500/10",
    borderClass: "border-amber-500/30",
    barClass: "bg-amber-400",
    fallback: "Paused",
  },
  Stopped: {
    icon: Square,
    textClass: "text-muted-foreground",
    softClass: "bg-muted/40",
    borderClass: "border-border",
    barClass: "bg-muted-foreground",
    fallback: "Stopped",
  },
};

const processingSteps: Array<{
  id: ProcessingStep;
  icon: typeof GitBranch;
  labelKey: string;
  fallback: string;
}> = [
  { id: "Workspace", icon: GitBranch, labelKey: "workspace", fallback: "Prepare" },
  { id: "Catalog", icon: FileCode, labelKey: "catalog", fallback: "Catalog" },
  { id: "Content", icon: Brain, labelKey: "content", fallback: "Content" },
  { id: "Translation", icon: Languages, labelKey: "translation", fallback: "Translate" },
  { id: "MindMap", icon: Network, labelKey: "mindMap", fallback: "Mind map" },
  { id: "Graphify", icon: Activity, labelKey: "graphify", fallback: "Graphify" },
  { id: "Complete", icon: CheckCircle2, labelKey: "complete", fallback: "Complete" },
];

export function RepositoryProcessingStatus({
  owner,
  repo,
  status: initialStatus,
  canControl = false,
}: RepositoryProcessingStatusProps) {
  const t = useTranslations();
  const [status, setStatus] = useState<RepositoryStatus>(initialStatus);
  const [currentStep, setCurrentStep] = useState<ProcessingStep>("Workspace");
  const [totalDocuments, setTotalDocuments] = useState(0);
  const [completedDocuments, setCompletedDocuments] = useState(0);
  const [startedAt, setStartedAt] = useState<Date | null>(null);
  const [dots, setDots] = useState("");
  const [elapsedTime, setElapsedTime] = useState(0);
  const [isPolling, setIsPolling] = useState(
    initialStatus !== "Paused" && initialStatus !== "Stopped",
  );
  const [lastUpdated, setLastUpdated] = useState<Date>(new Date());
  const [activeAction, setActiveAction] = useState<ControlAction | null>(null);
  const [statusConfirmed, setStatusConfirmed] = useState(false);
  const restartAllowed = statusConfirmed && canRestartRepository(status);
  const controlInFlight = useRef(false);
  const statusRequestVersion = useRef(0);

  const text = useCallback(
    (key: string, fallback: string) => {
      const value = t(key);
      return value === key ? fallback : value;
    },
    [t],
  );

  const pollStatusAndLogs = useCallback(async () => {
    const version = ++statusRequestVersion.current;
    try {
      const logsResponse = await fetchProcessingLogs(owner, repo, undefined, 500);
      if (version !== statusRequestVersion.current) return;
      setStatus(logsResponse.statusName);
      setStatusConfirmed(true);
      setLastUpdated(new Date());
      setTotalDocuments(logsResponse.totalDocuments);
      setCompletedDocuments(logsResponse.completedDocuments);
      if (logsResponse.startedAt) {
        // 确保正确解析 UTC 时间，后端存储的是 UTC 时间
        const startedAtStr = logsResponse.startedAt;
        if (startedAtStr.endsWith('Z') || startedAtStr.includes('+00:00')) {
          setStartedAt(new Date(startedAtStr));
        } else {
          // 如果没有时区标记，假设是 UTC 时间
          setStartedAt(new Date(startedAtStr + 'Z'));
        }
      }
      setCurrentStep(logsResponse.currentStepName);

      if (logsResponse.statusName === "Completed") {
        const { defaultSlug } = await fetchRepoStatus(owner, repo);
        if (version !== statusRequestVersion.current || !defaultSlug) return;
        setIsPolling(false);
        setCurrentStep("Complete");
        setTimeout(() => {
          if (version === statusRequestVersion.current) {
            window.location.href = buildRepoDocPath(owner, repo, defaultSlug);
          }
        }, 2000);
      } else {
        setIsPolling(logsResponse.statusName === "Pending" || logsResponse.statusName === "Processing");
      }
    } catch (error) {
      console.error("Failed to poll status:", error);
    }
  }, [owner, repo]);

  useEffect(() => {
    void pollStatusAndLogs();
    return () => { ++statusRequestVersion.current; };
  }, [pollStatusAndLogs]);

  useEffect(() => {
    if (!isPolling) return;

    const pollInterval = setInterval(() => {
      pollStatusAndLogs();
    }, 5000);

    return () => clearInterval(pollInterval);
  }, [isPolling, pollStatusAndLogs]);

  useEffect(() => {
    if (status === "Processing" || status === "Pending") {
      const interval = setInterval(() => {
        setDots((prev) => (prev.length >= 3 ? "" : prev + "."));
      }, 500);
      return () => clearInterval(interval);
    }
  }, [status]);

  useEffect(() => {
    if ((status === "Processing" || status === "Pending") && startedAt) {
      const updateElapsed = () => {
        const now = new Date();
        const nowUtc = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), 
                                now.getUTCHours(), now.getUTCMinutes(), now.getUTCSeconds());
        const startedUtc = Date.UTC(startedAt.getUTCFullYear(), startedAt.getUTCMonth(), startedAt.getDate(),
                                    startedAt.getUTCHours(), startedAt.getUTCMinutes(), startedAt.getUTCSeconds());
        const elapsed = Math.floor((nowUtc - startedUtc) / 1000);
        setElapsedTime(elapsed);
      };

      updateElapsed();
      const timer = setInterval(updateElapsed, 1000);
      return () => clearInterval(timer);
    }
  }, [status, startedAt]);

  const getControlErrorMessage = (error: unknown) => {
    if (error instanceof ApiError) {
      if (error.status === 403) {
        return text(
          "home.repository.status.actions.forbidden",
          "You do not have permission to control this repository",
        );
      }

      const data = error.data as { errorMessage?: string } | undefined;
      if (data && typeof data.errorMessage === "string" && data.errorMessage.trim()) {
        return data.errorMessage;
      }

      return (
        error.message ||
        text("home.repository.status.actions.actionFailed", "Action failed, please try again")
      );
    }

    return text("home.repository.status.actions.actionFailed", "Action failed, please try again");
  };

  const handleControlAction = async (action: ControlAction) => {
    if (controlInFlight.current || (action === "restart" && !restartAllowed)) return;
    controlInFlight.current = true;
    ++statusRequestVersion.current;
    setActiveAction(action);
    try {
      await controlActionHandlers[action](owner, repo);
      toast.success(
        text(
          `home.repository.status.actions.${action}Success`,
          `${controlActionFallbacks[action]} succeeded`,
        ),
      );

      if (action === "resume" || action === "restart") {
        setStatus("Pending");
        setCurrentStep("Workspace");
        if (action === "restart") {
          setTotalDocuments(0);
          setCompletedDocuments(0);
          setStartedAt(null);
          setElapsedTime(0);
        }
        setIsPolling(true);
      } else {
        setStatus(action === "pause" ? "Paused" : "Stopped");
        setIsPolling(false);
      }

      await pollStatusAndLogs();
    } catch (error) {
      toast.error(getControlErrorMessage(error));
      await pollStatusAndLogs();
    } finally {
      controlInFlight.current = false;
      setActiveAction(null);
    }
  };

  const handleManualRefresh = () => {
    pollStatusAndLogs();
  };

  const config = statusConfig[status];
  const Icon = config.icon;
  const currentStepIndex = Math.max(0, processingSteps.findIndex((step) => step.id === currentStep));
  const finalStepIndex = processingSteps.length - 1;
  const safeTotalDocuments = Math.max(totalDocuments, 0);
  const safeCompletedDocuments = Math.max(completedDocuments, 0);
  const displayedCompletedDocuments = safeTotalDocuments > 0
    ? Math.min(safeCompletedDocuments, safeTotalDocuments)
    : safeCompletedDocuments;
  const documentPercent = safeTotalDocuments > 0
    ? Math.min(Math.round((displayedCompletedDocuments / safeTotalDocuments) * 100), 100)
    : 0;
  const activeStepFraction = currentStep === "Content" && safeTotalDocuments > 0
    ? documentPercent / 100
    : 0;
  const overallPercent = status === "Completed"
    ? 100
    : Math.min(Math.round(((currentStepIndex + activeStepFraction) / finalStepIndex) * 100), 99);
  const remainingDocuments = Math.max(safeTotalDocuments - displayedCompletedDocuments, 0);
  const currentStepConfig = processingSteps[currentStepIndex] ?? processingSteps[0];
  const currentStepLabel = text(
    `home.repository.status.steps.${currentStepConfig.labelKey}`,
    currentStepConfig.fallback,
  );
  const statusLabel = text(`home.repository.status.${status.toLowerCase()}`, config.fallback);
  const isProcessing = status === "Processing" || status === "Pending";
  const availableActions = canControl ? visibleControlActions[status] : [];

  const formatTime = (seconds: number) => {
    const hours = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;

    if (hours > 0) {
      return `${hours.toString().padStart(2, "0")}:${mins.toString().padStart(2, "0")}:${secs
        .toString()
        .padStart(2, "0")}`;
    }

    return `${mins.toString().padStart(2, "0")}:${secs.toString().padStart(2, "0")}`;
  };

  const formatLastUpdated = (date: Date) => {
    return date.toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  };

  return (
    <div className="min-h-[90vh] overflow-x-hidden bg-background text-foreground">
      <div className="mx-auto flex min-h-[90vh] w-full max-w-7xl flex-col px-4 py-5 sm:px-6 lg:px-8">
        <header className="flex flex-wrap items-center justify-start gap-3 border-b border-border/70 pb-4 sm:justify-between">
          <Link
            href="/"
            className="inline-flex h-9 items-center gap-2 rounded-lg border border-border bg-background px-3 text-sm text-muted-foreground transition-colors hover:border-foreground/30 hover:text-foreground"
          >
            <Home className="h-4 w-4" />
            {text("common.backToHome", "Back to Home")}
          </Link>

          <button
            type="button"
            onClick={handleManualRefresh}
            className="inline-flex h-9 items-center gap-2 rounded-lg border border-border bg-background px-3 text-sm text-muted-foreground transition-colors hover:border-foreground/30 hover:text-foreground"
          >
            <RefreshCw className={`h-4 w-4 ${isPolling ? "animate-spin" : ""}`} />
            {formatLastUpdated(lastUpdated)}
          </button>
        </header>

        <main className="grid flex-1 items-center gap-10 py-10 lg:grid-cols-[minmax(0,0.9fr)_minmax(420px,1.1fr)]">
          <section className="min-w-0 space-y-8">
            <div className="space-y-4">
              <div className="inline-flex items-center gap-2 rounded-lg border border-border bg-muted/30 px-3 py-1 text-xs text-muted-foreground">
                <GitBranch className="h-3.5 w-3.5" />
                GitHub
              </div>

              <div className="space-y-3">
                <h1 className="max-w-full break-all text-3xl font-semibold leading-tight tracking-normal sm:text-5xl">
                  {owner}/{repo}
                </h1>
                <div
                  className={`inline-flex items-center gap-2 rounded-lg border px-3 py-1.5 text-sm ${config.borderClass} ${config.softClass} ${config.textClass}`}
                >
                  <Icon className={`h-4 w-4 ${status === "Processing" ? "animate-spin" : ""}`} />
                  <span>
                    {statusLabel}
                    {isProcessing && dots}
                  </span>
                </div>
              </div>
            </div>

            <div className="space-y-3">
              <div className="flex items-end gap-3">
                <span className={`text-7xl font-semibold leading-none tracking-normal ${config.textClass}`}>
                  {overallPercent}
                </span>
                <span className="pb-2 text-xl text-muted-foreground">%</span>
              </div>
              <div className="h-2 overflow-hidden rounded-full bg-muted">
                <div
                  className={`h-full rounded-full transition-all duration-700 ${config.barClass}`}
                  style={{ width: `${overallPercent}%` }}
                />
              </div>
            </div>

            <div className="grid grid-cols-2 gap-px overflow-hidden rounded-lg border border-border bg-border text-sm sm:grid-cols-4">
              <div className="bg-background p-4">
                <div className="text-xs text-muted-foreground">{text("home.repository.status.elapsed", "Elapsed")}</div>
                <div className="mt-1 font-medium">{formatTime(elapsedTime)}</div>
              </div>
              <div className="bg-background p-4">
                <div className="text-xs text-muted-foreground">{text("home.repository.status.processingSteps", "Stage")}</div>
                <div className="mt-1 font-medium">{currentStepLabel}</div>
              </div>
              <div className="bg-background p-4">
                <div className="text-xs text-muted-foreground">{text("home.repository.status.documentProgress", "Documents")}</div>
                <div className="mt-1 font-medium">
                  {safeTotalDocuments > 0 ? `${displayedCompletedDocuments}/${safeTotalDocuments}` : "-"}
                </div>
              </div>
              <div className="bg-background p-4">
                <div className="text-xs text-muted-foreground">{text("home.repository.status.remaining", "Remaining")}</div>
                <div className="mt-1 font-medium">{safeTotalDocuments > 0 ? remainingDocuments : "-"}</div>
              </div>
            </div>
          </section>

          <section className="min-w-0 rounded-lg border border-border bg-muted/20 p-4 sm:p-5">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border pb-4">
              <div>
                <div className="text-sm font-medium">{text("home.repository.status.processingSteps", "Processing Steps")}</div>
                <div className="mt-1 text-xs text-muted-foreground">{currentStepLabel}</div>
              </div>
              <Sparkles className={`h-5 w-5 ${config.textClass}`} />
            </div>

            <div className="mt-5 grid grid-cols-2 gap-px overflow-hidden rounded-lg border border-border bg-border sm:grid-cols-4 lg:grid-cols-7">
              {processingSteps.map((step, index) => {
                const StepIcon = step.icon;
                const isActive = index === currentStepIndex;
                const isCompleted = index < currentStepIndex || status === "Completed";
                const stepLabel = text(`home.repository.status.steps.${step.labelKey}`, step.fallback);

                return (
                  <div
                    key={step.id}
                    className={`min-h-24 bg-background p-3 transition-colors ${
                      isActive
                        ? `${config.softClass} ${config.textClass}`
                        : isCompleted
                          ? "text-emerald-400"
                          : "text-muted-foreground"
                    }`}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <StepIcon className="h-4 w-4" />
                      <span className="text-[10px] tabular-nums">{String(index + 1).padStart(2, "0")}</span>
                    </div>
                    <div className="mt-5 text-xs font-medium">{stepLabel}</div>
                  </div>
                );
              })}
            </div>

            <div className="mt-6 space-y-5">
              <div className="space-y-2">
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">{text("home.repository.status.processingSteps", "Pipeline")}</span>
                  <span className="font-medium tabular-nums">{overallPercent}%</span>
                </div>
                <div className="h-2 overflow-hidden rounded-full bg-background">
                  <div
                    className={`h-full rounded-full transition-all duration-700 ${config.barClass}`}
                    style={{ width: `${overallPercent}%` }}
                  />
                </div>
              </div>

              {safeTotalDocuments > 0 && (
                <div className="space-y-2">
                  <div className="flex items-center justify-between text-sm">
                    <span className="text-muted-foreground">
                      {text("home.repository.status.documentProgress", "Document Progress")}
                    </span>
                    <span className="font-medium tabular-nums">
                      {displayedCompletedDocuments}/{safeTotalDocuments}
                    </span>
                  </div>
                  <div className="h-2 overflow-hidden rounded-full bg-background">
                    <div
                      className="h-full rounded-full bg-emerald-400 transition-all duration-700"
                      style={{ width: `${documentPercent}%` }}
                    />
                  </div>
                </div>
              )}
            </div>

            {status === "Paused" && (
              <div className="mt-6 flex items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-sm text-amber-400">
                <Pause className="h-4 w-4" />
                {text(
                  "home.repository.status.pausedTip",
                  "Generation is paused. Resume to continue from the last completed document.",
                )}
              </div>
            )}

            {status === "Stopped" && (
              <div className="mt-6 flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
                <Square className="h-4 w-4" />
                {text(
                  "home.repository.status.stoppedTip",
                  "Generation is stopped. Generated documents are kept until you restart.",
                )}
              </div>
            )}

            {canControl && availableActions.length > 0 && (
              <div className="mt-6 flex flex-wrap gap-3">
                {availableActions.map((action) => {
                  if (action === "restart") {
                    return (
                      <RepositoryRestartButton key={action} size="lg" className="flex-1"
                        status={status} activeAction={activeAction} disabled={!statusConfirmed}
                        onClick={() => handleControlAction(action)} />
                    );
                  }
                  const ActionIcon = controlActionIcons[action];
                  const isActionLoading = activeAction === action;

                  return (
                    <button
                      key={action}
                      type="button"
                      onClick={() => handleControlAction(action)}
                      disabled={activeAction !== null}
                      className="inline-flex h-10 flex-1 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-border bg-background px-4 text-sm font-medium text-foreground transition-colors hover:border-foreground/30 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {isActionLoading ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <ActionIcon className="h-4 w-4" />
                      )}
                      {text(
                        `home.repository.status.actions.${action}`,
                        controlActionFallbacks[action],
                      )}
                    </button>
                  );
                })}
              </div>
            )}

            {status === "Completed" && (
              <div className="mt-6 flex items-center gap-2 rounded-lg border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-400">
                <CheckCircle2 className="h-4 w-4" />
                {text("home.repository.status.completedTip", "Redirecting to documentation...")}
              </div>
            )}
          </section>
        </main>

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-border/70 pt-4 text-xs text-muted-foreground">
          <span>{text("home.repository.status.processingTip", "Large repositories may take several minutes to process.")}</span>
          <Link href="/" className="inline-flex items-center gap-1.5 transition-colors hover:text-foreground">
            <ArrowLeft className="h-3.5 w-3.5" />
            {text("common.backToHome", "Back to Home")}
          </Link>
        </footer>
      </div>
    </div>
  );
}
