"use client";

import type { ComponentProps } from "react";
import { Loader2, RefreshCw } from "lucide-react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { canRestartRepository, type ControlAction } from "@/lib/repository-controls";
import { cn } from "@/lib/utils";
import type { RepositoryStatus } from "@/types/repository";

type Props = Pick<ComponentProps<typeof Button>, "onClick" | "size" | "variant" | "className" | "disabled"> & {
  status: RepositoryStatus | null;
  activeAction: ControlAction | null;
};

export function RepositoryRestartButton({ status, activeAction, disabled, variant, className, ...props }: Props) {
  const t = useTranslations("home.repository");
  let progress = "";
  if (status === "Processing") progress = t("status.actions.generating");
  else if (status === "Pending") progress = t("status.actions.queued");
  else if (activeAction === "restart") progress = t("submitting");

  return (
    <>
      <Button
        {...props}
        type="button"
        disabled={disabled || activeAction !== null || !canRestartRepository(status)}
        aria-busy={progress ? true : undefined}
        variant={progress ? "outline" : variant}
        className={cn(className, progress && "border-blue-500/25 bg-blue-500/10 text-blue-700 disabled:opacity-100 dark:bg-blue-500/10 dark:text-blue-400")}
      >
        {progress
          ? <Loader2 aria-hidden="true" className="size-4 motion-safe:animate-spin" />
          : <RefreshCw aria-hidden="true" className="size-4" />}
        {progress || t("status.actions.restart")}
      </Button>
      <span role="status" className="sr-only">{progress}</span>
    </>
  );
}
