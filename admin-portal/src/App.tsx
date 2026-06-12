import { BrowserRouter, Routes, Route, Navigate, Outlet } from "react-router";
import { RootLayout } from "@/components/layout/root-layout";
import { Dashboard } from "@/components/Dashboard";
import { AgentList } from "@/components/AgentList";
import { AgentBuilder } from "@/components/AgentBuilder";
import { GroupAgentOverlayEditor } from "@/components/GroupAgentOverlayEditor";
import { AgentChat } from "@/components/AgentChat";
import { PendingRules } from "@/components/PendingRules";
import { BusinessRules } from "@/components/BusinessRules";
import { PromptEditor } from "@/components/PromptEditor";
import { ScheduledTasks } from "@/components/ScheduledTasks";
import { SsoConfig } from "@/components/SsoConfig";
import { SsoConfigEditor } from "@/components/SsoConfigEditor";
import { UserProfiles } from "@/components/UserProfiles";
import { TenantList } from "@/components/TenantList";
import { TenantDetail } from "@/components/TenantDetail";
import { GroupList } from "@/components/GroupList";
import { GroupDetail } from "@/components/GroupDetail";
import { GroupAgentTemplateBuilder } from "@/components/GroupAgentTemplateBuilder";
import { PlatformLlmConfig } from "@/components/PlatformLlmConfig";
import { PlatformAdminsPage } from "@/components/PlatformAdminsPage";
import { RulePackManager } from "@/components/RulePackManager";
import { PackEditor } from "@/components/PackEditor";
import { BusinessRuleEditor } from "@/components/BusinessRuleEditor";
import { AuthCallback } from "@/components/AuthCallback";
import { LoginPage } from "@/components/LoginPage";
import { CredentialManager } from "@/components/CredentialManager";
import { ApiKeyManager } from "@/components/ApiKeyManager";
import { AgentGroups } from "@/components/AgentGroups";
import { A2ASettings } from "@/components/A2ASettings";
import { WidgetManager } from "@/components/WidgetManager";
import SessionBrowser from "@/components/SessionBrowser";
import SessionDetail from "@/components/SessionDetail";
import AgentOptimizer from "@/components/AgentOptimizer";
import AgentOptimizationSuggestions from "@/components/AgentOptimizationSuggestions";
import AgentFewShotExamples from "@/components/AgentFewShotExamples";
import { SchedulerFeedbackPage } from "@/components/SchedulerFeedbackPage";
import { SchedulerFeedbackReview } from "@/components/SchedulerFeedbackReview";
import { AUTH_ENABLED, auth } from "@/lib/auth";

/** Redirects to /login when auth is enabled and no token is stored. */
function AuthGuard({ children }: { children: React.ReactNode }) {
  if (AUTH_ENABLED && !auth.isAuthenticated()) {
    return <Navigate to="/login" replace />;
  }
  return <>{children}</>;
}

/** Redirects non-admin users (role "user" / "viewer") away from admin-only routes. */
function AdminGuard() {
  if (!auth.isAdmin()) {
    return <Navigate to="/agents" replace />;
  }
  return <Outlet />;
}

/** Default landing — master admin → tenants, tenant admin → dashboard, chat users → agents. */
function DefaultRedirect() {
  const to = auth.isMasterAdmin()
    ? "/platform/tenants"
    : auth.isAdmin()
      ? "/dashboard"
      : "/agents";
  return <Navigate to={to} replace />;
}

export default function App() {
  return (
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <Routes>
        {/* Public auth routes */}
        <Route path="login" element={<LoginPage />} />
        <Route path="auth/callback" element={<AuthCallback />} />
        {/* Public scheduler feedback form — token is the proof of origin */}
        <Route path="scheduler-feedback" element={<SchedulerFeedbackPage />} />
        <Route path="auth/error" element={
          <div className="flex h-screen items-center justify-center text-destructive">
            Sign-in failed. Please try again.
          </div>
        } />

        {/* Protected app routes — shared RootLayout */}
        <Route element={<AuthGuard><RootLayout /></AuthGuard>}>
          <Route index element={<DefaultRedirect />} />

          {/* Open to all authenticated users (chat users + admins) */}
          <Route path="agents" element={<AgentList />} />
          <Route path="agents/:id/chat" element={<AgentChat />} />
          <Route path="sessions" element={<SessionBrowser />} />
          <Route path="sessions/:id" element={<SessionDetail />} />

          {/* Admin-only routes (tenant admin or master admin) */}
          <Route element={<AdminGuard />}>
            {/* Tenant-level routes */}
            <Route path="dashboard" element={<Dashboard />} />
            <Route path="agents/new" element={<AgentBuilder />} />
            <Route path="agents/:id/edit" element={<AgentBuilder />} />
            <Route path="agents/group/:templateId/overlay" element={<GroupAgentOverlayEditor />} />
            <Route path="agents/:id/optimize" element={<AgentOptimizer />} />
            <Route path="agents/:id/optimize/suggestions" element={<AgentOptimizationSuggestions />} />
            <Route path="agents/:id/examples" element={<AgentFewShotExamples />} />
            <Route path="agents/groups" element={<AgentGroups />} />
            <Route path="rules/learned" element={<PendingRules />} />
            <Route path="rules/business" element={<BusinessRules />} />
            <Route path="rules/business/new" element={<BusinessRuleEditor />} />
            <Route path="rules/business/:id/edit" element={<BusinessRuleEditor />} />
            <Route path="prompts" element={<PromptEditor />} />
            <Route path="rules/packs" element={<RulePackManager />} />
            <Route path="rules/packs/:id" element={<PackEditor />} />
            <Route path="schedules" element={<ScheduledTasks />} />
            <Route path="schedules/feedback" element={<SchedulerFeedbackReview />} />
            <Route path="settings/sso" element={<SsoConfig />} />
            <Route path="settings/sso/new" element={<SsoConfigEditor />} />
            <Route path="settings/sso/:id/edit" element={<SsoConfigEditor />} />
            <Route path="settings/users" element={<UserProfiles />} />
            <Route path="settings/credentials" element={<CredentialManager />} />
            <Route path="settings/api-keys" element={<ApiKeyManager />} />
            <Route path="settings/a2a" element={<A2ASettings />} />
            <Route path="settings/widgets" element={<WidgetManager />} />

            {/* Platform-level routes (master admin only) */}
            <Route path="platform/tenants" element={<TenantList />} />
            <Route path="platform/tenants/:id" element={<TenantDetail />} />
            <Route path="platform/groups" element={<GroupList />} />
            <Route path="platform/groups/:id" element={<GroupDetail />} />
            <Route path="platform/groups/:groupId/agents/new" element={<GroupAgentTemplateBuilder />} />
            <Route path="platform/groups/:groupId/agents/:templateId/edit" element={<GroupAgentTemplateBuilder />} />
            <Route path="platform/llm-config" element={<PlatformLlmConfig />} />
            <Route path="platform/admins" element={<PlatformAdminsPage />} />
          </Route>

          <Route path="*" element={<DefaultRedirect />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
