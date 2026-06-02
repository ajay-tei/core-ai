import { Moon, Sun, LogOut, KeyRound } from "lucide-react";
import { useTheme } from "next-themes";
import { useLocation } from "react-router";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { AUTH_ENABLED, auth } from "@/lib/auth";
import { ChangePasswordDialog } from "@/components/ChangePasswordDialog";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Separator } from "@/components/ui/separator";
import { SidebarTrigger } from "@/components/ui/sidebar";

const routeLabels: Record<string, string> = {
  dashboard: "Dashboard",
  agents: "Agents",
  new: "New Agent",
  edit: "Edit Agent",
  chat: "Test Agent",
  rules: "Rules",
  learned: "Learned Rules",
  business: "Business Rules",
  prompts: "Prompt Editor",
  schedules: "Schedules",
  feedback: "Feedback Review",
};

function useBreadcrumbs() {
  const location = useLocation();
  const segments = location.pathname.split("/").filter(Boolean);
  const crumbs: { label: string; href?: string }[] = [];

  let path = "";
  for (const segment of segments) {
    path += `/${segment}`;
    const label = routeLabels[segment] ?? segment;
    // Skip UUID-like segments from display but keep navigability
    if (/^[0-9a-f-]{8,}$/i.test(segment)) continue;
    crumbs.push({ label, href: path });
  }
  return crumbs;
}

export function Topbar() {
  const { theme, setTheme } = useTheme();
  const crumbs = useBreadcrumbs();
  const [changePasswordOpen, setChangePasswordOpen] = useState(false);

  return (
    <header className="flex h-14 shrink-0 items-center gap-2 border-b bg-background px-4">
      <SidebarTrigger className="-ml-1" />
      <Separator orientation="vertical" className="mr-2 h-4" />

      <Breadcrumb>
        <BreadcrumbList>
          {crumbs.map((crumb, i) => (
            <span key={crumb.href} className="flex items-center gap-1.5">
              {i > 0 && <BreadcrumbSeparator />}
              <BreadcrumbItem>
                {i === crumbs.length - 1 ? (
                  <BreadcrumbPage>{crumb.label}</BreadcrumbPage>
                ) : (
                  <span className="text-muted-foreground">{crumb.label}</span>
                )}
              </BreadcrumbItem>
            </span>
          ))}
        </BreadcrumbList>
      </Breadcrumb>

      <div className="ml-auto flex items-center gap-2">
        {AUTH_ENABLED && (() => {
          const user = auth.getUser();
          // isLocalUser: userId is a plain integer — local-auth users only (not SSO)
          const isLocalUser = !!user.userId && !isNaN(Number(user.userId));
          return (
            <>
              {(user.name || user.email) && (
                <span className="text-sm text-muted-foreground hidden sm:inline">
                  {user.name ?? user.email}
                </span>
              )}
              {isLocalUser && (
                <Button
                  variant="ghost"
                  size="icon"
                  className="size-8"
                  onClick={() => setChangePasswordOpen(true)}
                  title="Change password"
                >
                  <KeyRound className="size-4" />
                  <span className="sr-only">Change password</span>
                </Button>
              )}
              <Button variant="ghost" size="icon" className="size-8" onClick={() => auth.logout()} title="Sign out">
                <LogOut className="size-4" />
                <span className="sr-only">Sign out</span>
              </Button>
              <ChangePasswordDialog open={changePasswordOpen} onOpenChange={setChangePasswordOpen} />
            </>
          );
        })()}
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
          className="size-8"
        >
          <Sun className="size-4 rotate-0 scale-100 transition-transform dark:-rotate-90 dark:scale-0" />
          <Moon className="absolute size-4 rotate-90 scale-0 transition-transform dark:rotate-0 dark:scale-100" />
          <span className="sr-only">Toggle theme</span>
        </Button>
      </div>
    </header>
  );
}
