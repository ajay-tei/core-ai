/**
 * Thin auth state wrapper — reads/writes localStorage, no external dependencies.
 *
 * VITE_AUTH_ENABLED controls whether the portal enforces authentication:
 *   true (default)  → portal shows LoginPage when no token stored (OAuth.Enabled=true in API)
 *   false           → portal works without a token (OAuth.Enabled=false in API, MSW mock mode)
 *
 * Both flags must be toggled together:
 *   .env:                    VITE_AUTH_ENABLED=false  (to disable)
 *   appsettings.Development: OAuth.Enabled: false     (to disable)
 */

import { storageKey } from "@/lib/brand";

const API_BASE = import.meta.env.VITE_API_URL ?? import.meta.env.BASE_URL.replace(/\/$/, '');
const TENANT_ID = import.meta.env.VITE_TENANT_ID ?? "1";

export const AUTH_ENABLED = import.meta.env.VITE_AUTH_ENABLED !== "false";

export const auth = {
  /** True when a token is stored in localStorage. */
  isAuthenticated(): boolean
  {
    return !!localStorage.getItem(storageKey("token"));
  },

  /** The stored access token, or null. */
  getToken(): string | null
  {
    return localStorage.getItem(storageKey("token"));
  },

  /** Redirect browser to the API's SSO login endpoint for this tenant. */
  login(): void
  {
    window.location.href = `${ API_BASE }/api/auth/login?tenantId=${ TENANT_ID }`;
  },

  /** True when the current user is a platform-level master admin. */
  isMasterAdmin(): boolean
  {
    return localStorage.getItem(storageKey("is_master_admin")) === "true";
  },

  /** True when the current user is a tenant admin (or master admin). */
  isAdmin(): boolean
  {
    return localStorage.getItem(storageKey("is_admin")) === "true"
      || localStorage.getItem(storageKey("is_master_admin")) === "true";
  },

  /** The stored tenant ID as a number (0 = master admin, 1+ = regular tenant). */
  getTenantId(): number
  {
    return Number(localStorage.getItem(storageKey("tenant_id")) ?? TENANT_ID);
  },

  /** Clear all stored auth state and redirect to the login page. */
  logout(): void
  {
    const logoutUrl = localStorage.getItem(storageKey("logout_url"));
    localStorage.removeItem(storageKey("token"));
    localStorage.removeItem(storageKey("tenant_id"));
    localStorage.removeItem(storageKey("user_email"));
    localStorage.removeItem(storageKey("user_name"));
    localStorage.removeItem(storageKey("user_id"));
    localStorage.removeItem(storageKey("logout_url"));
    localStorage.removeItem(storageKey("is_master_admin"));
    localStorage.removeItem(storageKey("is_admin"));

    if (logoutUrl)
    {
      // Route through the API's /logout endpoint so the SSO provider only needs
      // to whitelist the API URL (not the portal URL) as a post-logout redirect.
      // The API redirects to the SSO logout with post_logout_redirect_uri pointing
      // back to /api/auth/logout-callback, which then sends the browser to /login.
      const encoded = encodeURIComponent(logoutUrl);
      window.location.href = `${ API_BASE }/api/auth/logout?logoutUrl=${ encoded }`;
    } else
    {
      // Use BASE_URL (set from VITE_BASE_PATH) so subpath deployments redirect correctly.
      window.location.href = `${ import.meta.env.BASE_URL }login`;
    }
  },

  /** User display info from the last successful login. */
  getUser(): { email: string | null; name: string | null; userId: string | null; }
  {
    return {
      email: localStorage.getItem(storageKey("user_email")),
      name: localStorage.getItem(storageKey("user_name")),
      userId: localStorage.getItem(storageKey("user_id")),
    };
  },
};
