"use client";

import { Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { useStore } from "@/lib/store";

export function HistoryDrawer({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { history, restore, removeHistory, clearHistory } = useStore();

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="w-full gap-0 sm:max-w-md">
        <SheetHeader className="border-b">
          <SheetTitle>History</SheetTitle>
          <SheetDescription>Your last 20 analyses, stored only in this browser.</SheetDescription>
        </SheetHeader>

        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto p-2">
          {history.length === 0 && <p className="p-4 text-center text-sm text-muted-foreground">No analyses yet.</p>}
          <ul className="space-y-1">
            {history.map((h) => (
              <li key={h.id} className="group flex items-start gap-1 rounded-md hover:bg-muted">
                <button type="button" onClick={() => { restore(h); onOpenChange(false); }} className="min-w-0 flex-1 p-2 text-left">
                  <p className="truncate font-mono text-xs">{h.query.trim().split("\n")[0]}</p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    {new Date(h.createdAt).toLocaleString()} · {h.queryType} · {h.dialect} · {h.response.recommendations.length} index{h.response.recommendations.length === 1 ? "" : "es"}
                  </p>
                </button>
                <Button variant="ghost" size="icon-sm" className="m-1 opacity-60 group-hover:opacity-100" aria-label="Delete entry" onClick={() => removeHistory(h.id)}>
                  <X />
                </Button>
              </li>
            ))}
          </ul>
        </div>

        {history.length > 0 && (
          <div className="border-t p-3">
            <Button variant="ghost" size="sm" onClick={clearHistory}><Trash2 /> Clear history</Button>
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}
