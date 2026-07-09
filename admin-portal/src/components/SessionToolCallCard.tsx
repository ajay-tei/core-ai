import { useState } from "react";
import { Link } from "react-router";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { ChevronDown, ChevronRight, Bot, Wrench, ExternalLink, Maximize2 } from "lucide-react";
import type { ToolCallDetail } from "../api";
import { ToolResultTable } from "./chat/ToolResultTable";
import { SqlBlock } from "./chat/SqlBlock";

interface Props {
  toolCall: ToolCallDetail;
}

const EXPAND_THRESHOLD = 500;

export default function SessionToolCallCard({ toolCall }: Props) {
  const [inputExpanded, setInputExpanded] = useState(false);
  const [outputExpanded, setOutputExpanded] = useState(false);
  const [fullTextDialog, setFullTextDialog] = useState<{ title: string; content: string } | null>(null);
  const isDelegation = toolCall.isAgentDelegation;

  return (
    <Card className={`mb-2 ${isDelegation ? "border-indigo-500/40 bg-indigo-500/5" : ""}`}>
      <CardContent className="p-3">
        {/* Header */}
        <div className="flex items-center gap-2 mb-2">
          {isDelegation
            ? <Bot className="size-4 text-indigo-400 shrink-0" />
            : <Wrench className="size-4 text-muted-foreground shrink-0" />}
          <span className="font-mono text-sm font-medium truncate">
            {isDelegation
              ? `→ ${toolCall.delegatedAgentName ?? toolCall.delegatedAgentId ?? "unknown"}`
              : toolCall.toolName}
          </span>
          <Badge variant="outline" className="ml-auto text-xs shrink-0">#{toolCall.sequence}</Badge>
          {isDelegation && toolCall.linkedA2ATaskId && (
            <span className="text-xs text-muted-foreground font-mono">
              {toolCall.linkedA2ATaskId.slice(0, 8)}
            </span>
          )}
          {isDelegation && toolCall.childSessionId && (
            <Link
              to={`/sessions/${toolCall.childSessionId}`}
              className="ml-1 inline-flex items-center gap-1 text-xs text-primary hover:underline shrink-0"
            >
              View Session <ExternalLink className="size-3" />
            </Link>
          )}
        </div>

        {/* Input */}
        {toolCall.toolInput && (
          <div className="mb-1">
            <div className="flex items-center">
              <Button
                variant="ghost"
                size="sm"
                className="h-6 px-1 text-xs text-muted-foreground gap-1"
                onClick={() => setInputExpanded(!inputExpanded)}
              >
                {inputExpanded ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
                INPUT
              </Button>
              {toolCall.toolInput.length > EXPAND_THRESHOLD && (
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-6 px-1 text-xs text-muted-foreground gap-1 ml-auto"
                  onClick={() => setFullTextDialog({ title: `${toolCall.toolName} — Input`, content: tryFormat(toolCall.toolInput!) })}
                >
                  <Maximize2 className="size-3" /> Full
                </Button>
              )}
            </div>
            {inputExpanded ? (
              extractSql(toolCall.toolName, toolCall.toolInput) ? (
                <SqlBlock sql={extractSql(toolCall.toolName, toolCall.toolInput)!} />
              ) : (
                <pre className="mt-1 text-xs bg-muted rounded p-2 overflow-x-auto whitespace-pre-wrap break-all max-h-64">
                  {tryFormat(toolCall.toolInput)}
                </pre>
              )
            ) : (
              <p className="text-xs text-muted-foreground truncate pl-1">{toolCall.toolInput.slice(0, 120)}</p>
            )}
          </div>
        )}

        {/* Output */}
        {toolCall.toolOutput && (
          <div>
            <div className="flex items-center">
              <Button
                variant="ghost"
                size="sm"
                className="h-6 px-1 text-xs text-muted-foreground gap-1"
                onClick={() => setOutputExpanded(!outputExpanded)}
              >
                {outputExpanded ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
                OUTPUT
              </Button>
              {toolCall.toolOutput.length > EXPAND_THRESHOLD && (
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-6 px-1 text-xs text-muted-foreground gap-1 ml-auto"
                  onClick={() => setFullTextDialog({ title: `${toolCall.toolName} — Output`, content: toolCall.toolOutput! })}
                >
                  <Maximize2 className="size-3" /> Full
                </Button>
              )}
            </div>
            {outputExpanded ? (
              <div className="mt-1">
                <ToolResultTable output={toolCall.toolOutput} />
              </div>
            ) : (
              <p className="text-xs text-muted-foreground truncate pl-1">{toolCall.toolOutput.slice(0, 120)}</p>
            )}
          </div>
        )}
      </CardContent>

      {/* Full text dialog */}
      <Dialog open={!!fullTextDialog} onOpenChange={open => { if (!open) setFullTextDialog(null); }}>
        <DialogContent className="max-w-4xl max-h-[85vh] flex flex-col">
          <DialogHeader>
            <DialogTitle className="font-mono text-sm">{fullTextDialog?.title}</DialogTitle>
          </DialogHeader>
          <pre className="flex-1 overflow-auto text-xs bg-muted rounded p-4 whitespace-pre-wrap break-all">
            {fullTextDialog?.content}
          </pre>
        </DialogContent>
      </Dialog>
    </Card>
  );
}

function tryFormat(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2); }
  catch { return s; }
}

// Extracts a SQL string from an execute_sql_query-style tool input.
function extractSql(name: string, input: string): string | null {
  if (!/sql|query/i.test(name)) return null;
  try {
    const obj = JSON.parse(input) as Record<string, unknown>;
    const sql = obj.query ?? obj.sql ?? obj.statement;
    return typeof sql === "string" ? sql : null;
  } catch {
    return null;
  }
}
