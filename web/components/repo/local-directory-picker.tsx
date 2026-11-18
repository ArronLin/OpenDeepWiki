"use client";

import { useEffect, useState } from "react";
import { ArrowLeft, ArrowRight, ArrowUp, Folder, FolderOpen } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { useTranslations } from "@/hooks/use-translations";
import { api } from "@/lib/api-client";

const endpoint = "/api/v1/repositories/local-directories";
type Roots = { roots: string[]; canEdit: boolean };
type FolderEntry = { name: string; path: string };
type Listing = { path: string; folders: FolderEntry[]; breadcrumbs: FolderEntry[]; parentPath?: string; nextOffset?: number };

export function LocalDirectoryPicker({ value, onSelect }: { value: string; onSelect: (path: string) => void }) {
  const t = useTranslations();
  const [config, setConfig] = useState<Roots>();
  const [browse, setBrowse] = useState(false);
  const [history, setHistory] = useState<string[]>([]);
  const [position, setPosition] = useState(0);
  const [lastPath, setLastPath] = useState("");
  const [address, setAddress] = useState("");
  const [selected, setSelected] = useState("");
  const [offset, setOffset] = useState(0);
  const [retry, setRetry] = useState(0);
  const [loading, setLoading] = useState(false);
  const [listing, setListing] = useState<Listing>();
  const [prefix, setPrefix] = useState("");
  const [filter, setFilter] = useState("");
  const [draft, setDraft] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [configError, setConfigError] = useState("");
  const [browseError, setBrowseError] = useState("");
  const path = browse ? history[position] ?? config?.roots[0] : undefined;

  useEffect(() => {
    const controller = new AbortController();
    api.get<Roots>(endpoint + "/roots", { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) setConfig(response);
      })
      .catch((error: Error) => {
        if (!controller.signal.aborted) setConfigError(error.message);
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!path) return;
    const controller = new AbortController();
    const query = new URLSearchParams({ path, search: filter, offset: String(offset) });
    api.get<Listing>(endpoint + "/children?" + query, { signal: controller.signal })
      .then((response) => {
        if (controller.signal.aborted) return;
        setListing((current) => offset && current
          ? { ...response, folders: [...current.folders, ...response.folders] }
          : response);
        setAddress(response.path);
        setLastPath(response.path);
      })
      .catch((error: Error) => {
        if (!controller.signal.aborted) setBrowseError(error.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [path, filter, offset, retry]);

  function resetListing(next = "") {
    setListing(undefined);
    setBrowseError("");
    setPrefix("");
    setFilter("");
    setAddress(next);
    setSelected("");
    setOffset(0);
    setLoading(Boolean(next));
  }

  function go(next: string) {
    const target = next.trim();
    if (!target || target === path) return;
    resetListing(target);
    if (history.length === 0 && path) {
      setHistory([path, target]);
      setPosition(1);
      return;
    }
    setHistory([...history.slice(0, position + 1), target]);
    setPosition(history.length ? position + 1 : 0);
  }

  function open(next: boolean) {
    setBrowse(next);
    if (!next) return;
    const initial = value.trim() || lastPath || config?.roots[0] || "";
    resetListing(initial);
    setHistory(initial ? [initial] : []);
    setPosition(0);
  }

  function travel(index: number) {
    resetListing(history[index]);
    setPosition(index);
  }

  function search(next: string) {
    const term = next.trim();
    setPrefix(next);
    if (filter === term) return;
    setBrowseError("");
    setSelected("");
    setOffset(0);
    setFilter(term);
    setListing(undefined);
    setLoading(true);
  }

  async function save() {
    if (draft === undefined) return;
    setSaving(true);
    setConfigError("");
    try {
      const paths = draft.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
      setConfig(await api.put<Roots>(endpoint + "/roots", paths));
      setDraft(undefined);
      setBrowse(false);
    } catch (error) {
      setConfigError(error instanceof Error ? error.message : t("home.repository.submitError"));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2">
        <Dialog open={browse} onOpenChange={open}>
          <DialogTrigger asChild>
            <Button type="button" variant="outline" size="sm"><FolderOpen className="size-4" />{t("home.repository.browseLocal")}</Button>
          </DialogTrigger>
          <DialogContent className="max-h-[92dvh] w-[96vw] max-w-3xl overflow-y-auto">
            <DialogHeader>
              <DialogTitle>{t("home.repository.chooseLocalFolder")}</DialogTitle>
              <DialogDescription>{t("home.repository.localPathHint")}</DialogDescription>
            </DialogHeader>
            <div className="flex flex-wrap items-center gap-1">
              <Button type="button" variant="ghost" size="icon" aria-label={t("home.repository.localFolderBack")} disabled={position === 0} onClick={() => travel(position - 1)}><ArrowLeft /></Button>
              <Button type="button" variant="ghost" size="icon" aria-label={t("home.repository.localFolderForward")} disabled={position + 1 >= history.length} onClick={() => travel(position + 1)}><ArrowRight /></Button>
              <Button type="button" variant="ghost" size="icon" aria-label={t("home.repository.localFolderUp")} disabled={!listing?.parentPath} onClick={() => listing?.parentPath && go(listing.parentPath)}><ArrowUp /></Button>
              <Input className="min-w-40 flex-1" aria-label={t("home.repository.localPath")} value={address}
                onChange={(event) => setAddress(event.target.value)}
                onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); go(address); } }} />
              <Button type="button" variant="outline" onClick={() => go(address)}>{t("home.repository.localFolderGo")}</Button>
            </div>
            <nav aria-label={t("home.repository.localPath")} className="flex flex-wrap text-sm">
              {listing?.breadcrumbs?.map((item) => (
                <Button key={item.path} type="button" variant="ghost" size="sm" onClick={() => go(item.path)}>{item.name}</Button>
              ))}
            </nav>
            <div className="grid min-w-0 gap-3 sm:grid-cols-[minmax(9rem,1fr)_minmax(0,3fr)]">
              <aside className="max-h-28 overflow-y-auto rounded-md border p-2 sm:max-h-80" aria-label={t("home.repository.localRoots")}>
                {config?.roots.map((root) => (
                  <Button key={root} type="button" variant="ghost" className="h-auto w-full justify-start whitespace-normal break-all text-left" onClick={() => go(root)}>
                    <Folder className="shrink-0" />{root}
                  </Button>
                ))}
                {config?.roots.length === 0 && <p>{t("home.repository.noLocalRoots")}</p>}
                {configError && <p role="alert">{configError}</p>}
              </aside>
              <div className="min-w-0 space-y-2">
                <div className="flex gap-2">
                  <Input aria-label={t("home.repository.localFolderSearch")} placeholder={t("home.repository.localFolderSearch")} value={prefix} disabled={!path}
                    onChange={(event) => setPrefix(event.target.value)}
                    onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); search(prefix); } }} />
                  <Button type="button" variant="outline" disabled={!path} onClick={() => search(prefix)}>{t("home.repository.filterLocalFolders")}</Button>
                  {prefix && <Button type="button" variant="ghost" onClick={() => search("")}>{t("home.repository.localFolderClear")}</Button>}
                </div>
                <ul className="h-40 overflow-y-auto rounded-md border p-1 sm:h-64"
                  onKeyDown={(event) => {
                    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
                    const rows = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>("button[data-folder]"));
                    const index = rows.indexOf(document.activeElement as HTMLButtonElement);
                    const next = rows[index + (event.key === "ArrowDown" ? 1 : -1)];
                    if (next) { event.preventDefault(); next.focus(); next.click(); }
                  }}>
                  {listing?.folders.map((folder) => (
                    <li key={folder.path}>
                      <Button type="button" data-folder variant={selected === folder.path ? "secondary" : "ghost"}
                        aria-pressed={selected === folder.path}
                        className="min-h-11 h-auto w-full justify-start whitespace-normal break-all text-left"
                        onClick={() => setSelected(folder.path)} onDoubleClick={() => go(folder.path)}
                        onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); go(folder.path); } }}>
                        <Folder className="shrink-0" />{folder.name}
                      </Button>
                    </li>
                  ))}
                </ul>
                {(loading || (path && !listing && !browseError)) &&
                  <p role="status">{t("home.repository.loadingLocalFolders")}</p>}
                {browseError && <div role="alert" className="text-destructive">
                  {browseError} <Button type="button" variant="ghost" onClick={() => { setBrowseError(""); setLoading(true); setRetry(retry + 1); }}>{t("home.repository.localFolderRetry")}</Button>
                </div>}
                {!loading && listing?.folders.length === 0 && <p>{t("home.repository.noLocalFolders")}</p>}
                {listing?.nextOffset != null && <Button type="button" variant="outline" disabled={loading || !!browseError}
                  onClick={() => { setLoading(true); setOffset(listing.nextOffset!); }}>{t("home.repository.localFolderMore")}</Button>}
              </div>
            </div>
            <DialogFooter className="items-center">
              <span className="mr-auto min-w-0 break-all text-sm" aria-live="polite">{selected || listing?.path}</span>
              <Button type="button" variant="outline" onClick={() => open(false)}>{t("home.repository.cancelLocalRoots")}</Button>
              <Button type="button" disabled={!listing || !!browseError} onClick={() => {
                if (listing) { onSelect(selected || listing.path); open(false); }
              }}>{t("home.repository.chooseLocalFolder")}</Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
        {config?.canEdit && draft === undefined && (
          <Button type="button" variant="ghost" size="sm" onClick={() => setDraft(config.roots.join("\n"))}>
            {t("home.repository.editLocalRoots")}
          </Button>
        )}
      </div>
      <div className="rounded-md border p-3 text-sm">
        <p className="font-medium">{t("home.repository.localRoots")}</p>
        {config && (config.roots.length ? (
          <ul className="mt-1 space-y-1">
            {config.roots.map((root) => (
              <li key={root} className="break-all">
                {root}
              </li>
            ))}
          </ul>
        ) : <p className="mt-1 text-muted-foreground">{t("home.repository.noLocalRoots")}</p>)}
        {!config && !configError && <p role="status">{t("home.repository.loadingLocalFolders")}</p>}
        {draft !== undefined && (
          <div className="mt-2 space-y-2">
            <Textarea aria-label={t("home.repository.localRoots")} value={draft} disabled={saving}
              onChange={(event) => setDraft(event.target.value)} />
            <p className="text-xs text-muted-foreground">{t("home.repository.localRootsEditHint")}</p>
            <div className="flex gap-2">
              <Button type="button" size="sm" disabled={saving} onClick={save}>{t("home.repository.saveLocalRoots")}</Button>
              <Button type="button" variant="ghost" size="sm" disabled={saving} onClick={() => {
                setDraft(undefined);
                setConfigError("");
              }}>{t("home.repository.cancelLocalRoots")}</Button>
            </div>
          </div>
        )}
        {configError && <p role="alert" className="mt-2 text-destructive">{configError}</p>}
      </div>
    </div>
  );
}
