import { useState, useEffect, useRef } from "react";
import { useNavigate } from "react-router";
import { Shield, LogIn, ShieldAlert, ChevronsUpDown, Check, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ScrollArea } from "@/components/ui/scroll-area";
import { api, type SsoProvider } from "@/api";
import { APP_NAME, storageKey } from "@/lib/brand";

const API_BASE  = import.meta.env.VITE_API_URL ?? import.meta.env.BASE_URL.replace(/\/$/, '');
const TENANT_ID = import.meta.env.VITE_TENANT_ID ?? "";   // non-empty = single-tenant deployment

/**
 * Login page:
 *
 * - Loads all active SSO providers from GET /api/auth/providers on mount.
 * - Each provider is shown as a button. Clicking one redirects the browser to
 *   /api/auth/login?tenantId={provider.tenantId} (Authorization Code flow).
 * - Local username/password form is always shown below.
 *   Tenant ID for local login comes from:
 *     1. VITE_TENANT_ID env var  (single-tenant deployment)
 *     2. Tenant selector dropdown populated from the SSO provider list (multi-tenant)
 */
export function LoginPage() {
  const navigate = useNavigate();

  const [providers,    setProviders]    = useState<SsoProvider[]>([]);
  const [loadingProv,  setLoadingProv]  = useState(true);
  const [selectedSso,  setSelectedSso]  = useState<string>("");
  const [ssoOpen,      setSsoOpen]      = useState(false);
  const [ssoSearch,    setSsoSearch]    = useState("");
  const searchRef = useRef<HTMLInputElement>(null);

  // Local login state
  const [username,     setUsername]     = useState("");
  const [password,     setPassword]     = useState("");
  const [localTenant,  setLocalTenant]  = useState(TENANT_ID || "");
  const [submitting,   setSubmitting]   = useState(false);
  const [error,        setError]        = useState<string | null>(null);

  // Platform admin login state
  const [showAdminLogin,   setShowAdminLogin]   = useState(false);
  const [adminConfigured,  setAdminConfigured]  = useState<boolean | null>(null);
  const [adminUsername,    setAdminUsername]    = useState("");
  const [adminPassword,    setAdminPassword]    = useState("");
  const [adminEmail,       setAdminEmail]       = useState("");
  const [adminSubmitting,  setAdminSubmitting]  = useState(false);
  const [adminError,       setAdminError]       = useState<string | null>(null);

  const isSingleTenant = Boolean(TENANT_ID);

  // ── Load SSO providers ────────────────────────────────────────────────────
  useEffect(() => {
    api.listSsoProviders()
      .then(setProviders)
      .catch(() => setProviders([]))
      .finally(() => setLoadingProv(false));
  }, []);

  // ── SSO login — redirect to provider ─────────────────────────────────────
  function ssoLogin(tenantId: number) {
    window.location.href = `${API_BASE}/api/auth/login?tenantId=${tenantId}`;
  }

  // ── Check setup status when admin section is opened ──────────────────────
  async function openAdminSection() {
    setShowAdminLogin(true);
    setAdminError(null);
    if (adminConfigured !== null) return; // already checked this session
    try {
      const res = await fetch(`${API_BASE}/api/auth/setup`);
      if (res.ok) {
        const data = await res.json();
        setAdminConfigured(!!data.isConfigured);
      } else {
        // Non-2xx (e.g. 404 if server not rebuilt yet) — show login form as
        // safe fallback; a wrong password is better UX than being stuck on setup.
        setAdminConfigured(true);
      }
    } catch {
      // Network error — default to login form, same rationale as above.
      setAdminConfigured(true);
    }
  }

  // ── Platform admin setup (first time) ────────────────────────────────────
  async function handleAdminSetup(e: React.FormEvent) {
    e.preventDefault();
    setAdminError(null);
    setAdminSubmitting(true);
    try {
      const res = await fetch(`${API_BASE}/api/auth/setup`, {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify({ username: adminUsername, email: adminEmail, password: adminPassword }),
      });
      if (res.status === 409) {
        // Admin already exists — switch directly to login form.
        setAdminConfigured(true);
        setAdminPassword("");
        return;
      }
      if (!res.ok) {
        const body = await res.json().catch(() => null);
        setAdminError(body?.message ?? "Setup failed");
        return;
      }
      setAdminConfigured(true);
      setAdminPassword("");
    } catch {
      setAdminError("Network error — could not reach the server.");
    } finally {
      setAdminSubmitting(false);
    }
  }

  // ── Platform admin login ─────────────────────────────────────────────────
  async function handleAdminLogin(e: React.FormEvent) {
    e.preventDefault();
    setAdminError(null);
    setAdminSubmitting(true);
    try {
      const res = await fetch(`${API_BASE}/api/auth/admin`, {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify({ username: adminUsername, password: adminPassword }),
      });

      if (!res.ok) {
        setAdminError((await res.text()) || "Invalid credentials");
        return;
      }

      const data = await res.json();
      localStorage.setItem(storageKey("token"),        data.token);
      localStorage.setItem(storageKey("tenant_id"),    String(data.tenantId));   // "0"
      localStorage.setItem(storageKey("is_master_admin"), "true");
      localStorage.setItem(storageKey("is_admin"), "true");
      if (data.email) localStorage.setItem(storageKey("user_email"), data.email);
      if (data.name)  localStorage.setItem(storageKey("user_name"),  data.name);
      if (data.userId) localStorage.setItem(storageKey("user_id"),   data.userId);

      navigate("/platform/tenants", { replace: true });
    } catch {
      setAdminError("Network error — could not reach the server.");
    } finally {
      setAdminSubmitting(false);
    }
  }

  // ── Local login ───────────────────────────────────────────────────────────
  async function handleLocalLogin(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    const tenantId = Number(localTenant || TENANT_ID || 1);
    if (!tenantId) {
      setError("Select an organization to sign in to.");
      return;
    }

    setSubmitting(true);
    try {
      const res = await fetch(`${API_BASE}/api/auth/local`, {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify({ tenantId, username, password }),
      });

      if (!res.ok) {
        setError((await res.text()) || "Invalid username or password");
        return;
      }

      const data = await res.json();
      localStorage.setItem(storageKey("token"),     data.token);
      localStorage.setItem(storageKey("tenant_id"), String(data.tenantId));
      if (data.email)  localStorage.setItem(storageKey("user_email"), data.email);
      if (data.name)   localStorage.setItem(storageKey("user_name"),  data.name);
      if (data.userId) localStorage.setItem(storageKey("user_id"),    data.userId);

      const isAdmin = data.isAdmin === true;
      if (isAdmin) localStorage.setItem(storageKey("is_admin"), "true");
      else         localStorage.removeItem(storageKey("is_admin"));

      navigate(isAdmin ? "/dashboard" : "/agents", { replace: true });
    } catch {
      setError("Network error — could not reach the server.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="flex h-screen flex-col items-center justify-center gap-8 bg-background px-4">
      {/* Branding */}
      <div className="flex items-center gap-3">
        <Shield className="size-10 text-primary" />
        <span className="text-3xl font-semibold tracking-tight">{APP_NAME}</span>
      </div>

      <div className="flex w-full max-w-sm flex-col gap-6">

        {/* ── SSO providers ── */}
        {!loadingProv && providers.length > 0 && (
          <div className="flex flex-col gap-3">
            <p className="text-center text-sm text-muted-foreground">
              Sign in with your organization account
            </p>
            {providers.length === 1 ? (
              <Button
                size="lg"
                className="w-full"
                onClick={() => ssoLogin(providers[0].tenantId)}
              >
                <LogIn className="size-4 mr-2" />
                {providers[0].tenantName}
              </Button>
            ) : (
              <div className="flex gap-2">
                <Popover open={ssoOpen} onOpenChange={open => { setSsoOpen(open); if (open) setTimeout(() => searchRef.current?.focus(), 0); }}>
                  <PopoverTrigger asChild>
                    <Button variant="outline" role="combobox" aria-expanded={ssoOpen} className="flex-1 justify-between font-normal">
                      <span className="truncate">
                        {selectedSso
                          ? providers.find(p => String(p.tenantId) === selectedSso)?.tenantName
                          : "Select organization…"}
                      </span>
                      <ChevronsUpDown className="ml-2 size-4 shrink-0 opacity-50" />
                    </Button>
                  </PopoverTrigger>
                  <PopoverContent className="p-0 w-[var(--radix-popover-trigger-width)]" align="start">
                    <div className="flex items-center gap-2 border-b px-3 py-2">
                      <Search className="size-4 shrink-0 text-muted-foreground" />
                      <input
                        ref={searchRef}
                        className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
                        placeholder="Search organizations…"
                        value={ssoSearch}
                        onChange={e => setSsoSearch(e.target.value)}
                      />
                    </div>
                    <ScrollArea className="max-h-60">
                      {providers
                        .filter(p => p.tenantName.toLowerCase().includes(ssoSearch.toLowerCase()))
                        .map(p => (
                          <button
                            key={p.id}
                            type="button"
                            className="flex w-full items-center gap-2 px-3 py-2 text-sm hover:bg-accent hover:text-accent-foreground"
                            onClick={() => { setSelectedSso(String(p.tenantId)); setSsoOpen(false); setSsoSearch(""); }}
                          >
                            <Check className={`size-4 shrink-0 ${selectedSso === String(p.tenantId) ? "opacity-100" : "opacity-0"}`} />
                            {p.tenantName}
                          </button>
                        ))}
                      {providers.filter(p => p.tenantName.toLowerCase().includes(ssoSearch.toLowerCase())).length === 0 && (
                        <p className="px-3 py-4 text-center text-sm text-muted-foreground">No organizations found.</p>
                      )}
                    </ScrollArea>
                  </PopoverContent>
                </Popover>
                <Button
                  size="default"
                  disabled={!selectedSso}
                  onClick={() => selectedSso && ssoLogin(Number(selectedSso))}
                  className="shrink-0"
                >
                  <LogIn className="size-4" />
                </Button>
              </div>
            )}
          </div>
        )}

        {loadingProv && (
          <p className="text-center text-sm text-muted-foreground">Loading sign-in options…</p>
        )}

        {/* ── Divider ── */}
        {providers.length > 0 && (
          <div className="relative">
            <div className="absolute inset-0 flex items-center"><span className="w-full border-t" /></div>
            <div className="relative flex justify-center text-xs uppercase">
              <span className="bg-background px-2 text-muted-foreground">or</span>
            </div>
          </div>
        )}

        {/* ── Local login ── */}
        <form onSubmit={handleLocalLogin} className="flex flex-col gap-4">
          {/* Tenant selector — only shown in multi-tenant mode */}
          {!isSingleTenant && providers.length > 0 && (
            <div className="flex flex-col gap-1.5">
              <Label>Organization</Label>
              <Select value={localTenant} onValueChange={setLocalTenant}>
                <SelectTrigger>
                  <SelectValue placeholder="Select organization" />
                </SelectTrigger>
                <SelectContent>
                  {providers.map(p => (
                    <SelectItem key={p.id} value={String(p.tenantId)} className="capitalize">
                      {p.providerName}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="username">Username or Email</Label>
            <Input
              id="username"
              type="text"
              autoComplete="username"
              required
              value={username}
              onChange={e => setUsername(e.target.value)}
              placeholder="you@example.com"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="password">Password</Label>
            <Input
              id="password"
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={e => setPassword(e.target.value)}
              placeholder="••••••••"
            />
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button type="submit" variant="outline" disabled={submitting} className="w-full">
            {submitting ? "Signing in…" : "Sign in with local account"}
          </Button>
        </form>

        {/* ── Platform admin toggle ── */}
        <div className="pt-2 text-center">
          <button
            type="button"
            className="text-xs text-muted-foreground underline underline-offset-2"
            onClick={() => showAdminLogin ? setShowAdminLogin(false) : openAdminSection()}
          >
            {showAdminLogin ? "Back to regular sign-in" : "Platform administrator? Sign in here"}
          </button>
        </div>

        {/* ── Platform admin section ── */}
        {showAdminLogin && (
          <div className="flex flex-col gap-4 rounded-lg border border-amber-500/30 bg-amber-50/5 p-4">
            <div className="flex items-center gap-2 text-amber-600 dark:text-amber-400">
              <ShieldAlert className="size-4" />
              <span className="text-sm font-medium">
                {adminConfigured === null ? "Checking…" : adminConfigured ? "Platform Admin Login" : "First-Time Setup"}
              </span>
            </div>

            {/* Setup form — shown only when no master admin exists yet */}
            {adminConfigured === false && (
              <form onSubmit={handleAdminSetup} className="flex flex-col gap-3">
                <p className="text-xs text-muted-foreground">
                  No platform admin configured. Create one to get started.
                </p>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="setup-username">Username</Label>
                  <Input id="setup-username" type="text" required
                    value={adminUsername} onChange={e => setAdminUsername(e.target.value)} />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="setup-email">Email</Label>
                  <Input id="setup-email" type="email" required
                    value={adminEmail} onChange={e => setAdminEmail(e.target.value)} />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="setup-password">Password <span className="text-muted-foreground text-xs">(default: changemeonlogin)</span></Label>
                  <Input id="setup-password" type="password" autoComplete="new-password"
                    placeholder="changemeonlogin"
                    value={adminPassword} onChange={e => setAdminPassword(e.target.value)} />
                </div>
                {adminError && <p className="text-sm text-destructive">{adminError}</p>}
                <Button type="submit" disabled={adminSubmitting} className="w-full bg-amber-600 hover:bg-amber-700">
                  {adminSubmitting ? "Creating…" : "Create Platform Admin"}
                </Button>
              </form>
            )}

            {/* Login form — shown once master admin is configured */}
            {adminConfigured === true && (
              <form onSubmit={handleAdminLogin} className="flex flex-col gap-3">
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="admin-username">Username</Label>
                  <Input id="admin-username" type="text" autoComplete="username" required
                    value={adminUsername} onChange={e => setAdminUsername(e.target.value)} />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="admin-password">Password</Label>
                  <Input id="admin-password" type="password" autoComplete="current-password" required
                    value={adminPassword} onChange={e => setAdminPassword(e.target.value)} />
                </div>
                {adminError && <p className="text-sm text-destructive">{adminError}</p>}
                <Button type="submit" disabled={adminSubmitting} className="w-full bg-amber-600 hover:bg-amber-700">
                  {adminSubmitting ? "Signing in…" : "Sign in as Platform Admin"}
                </Button>
              </form>
            )}
          </div>
        )}

      </div>
    </div>
  );
}
