import {
  Bot,
  Brain,
  Building2,
  Calendar,
  ChevronDown,
  Cpu,
  History,
  KeyRound,
  Layers,
  LayoutDashboard,
  Network,
  Package,
  Plus,
  ScrollText,
  Shield,
  ShieldAlert,
  ShieldCheck,
  Star,
  Users,
  Zap,
  Code2,
} from "lucide-react";
import { Link, useLocation } from "react-router";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
} from "@/components/ui/sidebar";
import { Badge } from "@/components/ui/badge";
import { auth } from "@/lib/auth";
import { APP_NAME } from "@/lib/brand";

// ── Tenant-level navigation (regular users) ───────────────────────────────────

const tenantNavGroups = [
  {
    label: "Overview",
    items: [
      { title: "Dashboard", url: "/dashboard", icon: LayoutDashboard },
    ],
  },
  {
    label: "Agents",
    items: [
      { title: "All Agents", url: "/agents", icon: Bot },
      { title: "New Agent", url: "/agents/new", icon: Plus },
      { title: "Access Groups", url: "/agents/groups", icon: ShieldCheck },
    ],
  },
  {
    label: "Configuration",
    items: [
      { title: "Business Rules", url: "/rules/business", icon: Shield },
      { title: "Prompt Editor", url: "/prompts", icon: ScrollText },
      { title: "Learned Rules", url: "/rules/learned", icon: Brain, badgeKey: "pending" },
      { title: "Rule Packs", url: "/rules/packs", icon: Package },
      { title: "Sessions", url: "/sessions", icon: History },
      { title: "Schedules", url: "/schedules", icon: Calendar },
      { title: "Schedule Feedback", url: "/schedules/feedback", icon: Star },
    ],
  },
  {
    label: "Users & Auth",
    items: [
      { title: "User Profiles", url: "/settings/users", icon: Users },
      { title: "SSO Configuration", url: "/settings/sso", icon: KeyRound },
      { title: "MCP Credentials", url: "/settings/credentials", icon: ShieldAlert },
      { title: "API Keys", url: "/settings/api-keys", icon: Zap },
      { title: "A2A Protocol", url: "/settings/a2a", icon: Network },
      { title: "Chat Widgets", url: "/settings/widgets", icon: Code2 },
    ],
  },
];

// ── Platform-level navigation (master admin only) ─────────────────────────────

const platformNavGroups = [
  {
    label: "Platform Admin",
    items: [
      { title: "Tenants",    url: "/platform/tenants",    icon: Building2,  badgeKey: undefined },
      { title: "Groups",     url: "/platform/groups",     icon: Layers,     badgeKey: undefined },
      { title: "LLM Config", url: "/platform/llm-config", icon: Cpu,        badgeKey: undefined },
      { title: "Admins",     url: "/platform/admins",     icon: ShieldAlert, badgeKey: undefined },
    ],
  },
];

// ── Chat-user navigation (role "user" / "viewer" — no admin functionality) ────────

const chatUserNavGroups = [
  {
    label: "Workspace",
    items: [
      { title: "Agents", url: "/agents", icon: Bot },
      { title: "Chat History", url: "/sessions", icon: History },
    ],
  },
];

interface AppSidebarProps {
  pendingRuleCount?: number;
}

export function AppSidebar({ pendingRuleCount = 0 }: AppSidebarProps) {
  const location    = useLocation();
  const isMaster    = auth.isMasterAdmin();
  const isAdmin     = auth.isAdmin();
  const navGroups   = isMaster
    ? platformNavGroups
    : isAdmin
      ? tenantNavGroups
      : chatUserNavGroups;

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <div className="flex items-center gap-2 px-2 py-1">
          <div className={`flex size-8 items-center justify-center rounded-lg text-primary-foreground ${isMaster ? "bg-amber-600" : "bg-primary"}`}>
            {isMaster ? <ShieldAlert className="size-4" /> : <Zap className="size-4" />}
          </div>
          <div className="flex flex-col">
            <span className="text-sm font-semibold">{APP_NAME}</span>
            <span className="text-xs text-muted-foreground">
              {isMaster ? "Platform Admin" : "Agent Platform"}
            </span>
          </div>
          <ChevronDown className="ml-auto size-4 text-muted-foreground" />
        </div>
      </SidebarHeader>

      <SidebarContent>
        {navGroups.map((group) => (
          <SidebarGroup key={group.label}>
            <SidebarGroupLabel>{group.label}</SidebarGroupLabel>
            <SidebarGroupContent>
              <SidebarMenu>
                {group.items.map((item) => {
                  const isActive = location.pathname === item.url ||
                    (item.url !== "/dashboard" && location.pathname.startsWith(item.url));
                  return (
                    <SidebarMenuItem key={item.title}>
                      <SidebarMenuButton asChild isActive={isActive} tooltip={item.title}>
                        <Link to={item.url} className="flex items-center gap-2">
                          <item.icon className="size-4" />
                          <span>{item.title}</span>
                          {item.badgeKey === "pending" && pendingRuleCount > 0 && (
                            <Badge variant="destructive" className="ml-auto size-5 justify-center p-0 text-xs">
                              {pendingRuleCount}
                            </Badge>
                          )}
                        </Link>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  );
                })}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        ))}
      </SidebarContent>

      <SidebarFooter>
        <div className="px-2 py-2 text-xs text-muted-foreground">
          {isMaster ? "Platform Administration" : "Multi-tenant AI Platform"}
        </div>
      </SidebarFooter>

      <SidebarRail />
    </Sidebar>
  );
}
