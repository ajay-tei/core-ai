import { useEffect, useState } from "react";
import { Outlet } from "react-router";
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AppSidebar } from "@/components/layout/app-sidebar";
import { Topbar } from "@/components/layout/topbar";
import { api } from "@/api";

export function RootLayout() {
  const [pendingRuleCount, setPendingRuleCount] = useState(0);

  useEffect(() => {
    api.getDashboard(1)
      .then((d) => setPendingRuleCount(d.pendingRuleCount))
      .catch(() => {});
  }, []);

  return (
    <TooltipProvider delayDuration={200}>
      <SidebarProvider>
        <AppSidebar pendingRuleCount={pendingRuleCount} />
        <SidebarInset>
          <Topbar />
          <main className="flex flex-1 flex-col gap-4 p-4 md:p-6">
            <Outlet />
          </main>
        </SidebarInset>
      </SidebarProvider>
    </TooltipProvider>
  );
}
