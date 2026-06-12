import { useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { storageKey } from "@/lib/brand";

/**
 * Handles the OAuth2 callback redirect from the API server.
 *
 * The API's /api/auth/callback exchanges the authorization code for a token,
 * then redirects here with the token in the URL fragment:
 *   /auth/callback#token=…&tenant_id=1&email=…&name=…
 *
 * This component reads the fragment, stores the token in localStorage,
 * then navigates to the dashboard.
 *
 * On error it shows a helpful inline message rather than navigating to a
 * separate error route, so the user can see what went wrong and try again.
 */
export function AuthCallback() {
  const navigate = useNavigate();
  const [errorDetail, setErrorDetail] = useState<string | null>(null);

  useEffect(() => {
    // Check for ?error= query param from the OAuth provider
    const searchParams = new URLSearchParams(window.location.search);
    const queryError   = searchParams.get("error");
    const queryDesc    = searchParams.get("error_description");

    if (queryError) {
      setErrorDetail(
        queryDesc
          ? `Provider error: ${queryError} — ${queryDesc}`
          : `Provider error: ${queryError}`
      );
      return;
    }

    const hash   = window.location.hash.slice(1); // strip leading #
    const params = new URLSearchParams(hash);

    const token    = params.get("token");
    const tenantId = params.get("tenant_id");
    const email    = params.get("email");
    const name     = params.get("name");
    const userId   = params.get("user_id");
    const logoutUrl = params.get("logout_url");
    const isAdmin  = params.get("is_admin") === "true";

    if (token) {
      localStorage.setItem(storageKey("token"),     token);
      if (tenantId)  localStorage.setItem(storageKey("tenant_id"),   tenantId);
      if (email)     localStorage.setItem(storageKey("user_email"),  email);
      if (name)      localStorage.setItem(storageKey("user_name"),   name);
      if (userId)    localStorage.setItem(storageKey("user_id"),     userId);
      if (logoutUrl) localStorage.setItem(storageKey("logout_url"),  logoutUrl);
      if (isAdmin)   localStorage.setItem(storageKey("is_admin"),    "true");
      else           localStorage.removeItem(storageKey("is_admin"));

      // Clear the fragment from the address bar so the token isn't visible
      window.history.replaceState(null, "", window.location.pathname);

      navigate(isAdmin ? "/dashboard" : "/agents", { replace: true });
    } else {
      // No token in fragment — show details to help debug
      const rawHash = window.location.hash || "(empty)";
      setErrorDetail(
        `Sign-in did not return a token. Raw callback data: ${rawHash}`
      );
    }
  }, [navigate]);

  if (errorDetail) {
    return (
      <div className="flex h-screen flex-col items-center justify-center gap-4 px-6 text-center">
        <p className="text-destructive font-medium">Sign-in failed</p>
        <p className="text-muted-foreground max-w-md text-sm break-all">{errorDetail}</p>
        <a href={`${import.meta.env.BASE_URL}login`} className="text-primary underline text-sm">
          Return to sign-in
        </a>
      </div>
    );
  }

  return (
    <div className="flex h-screen items-center justify-center text-muted-foreground">
      Completing sign-in…
    </div>
  );
}
