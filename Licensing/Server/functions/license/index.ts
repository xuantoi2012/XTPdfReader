// Supabase Edge Function "license": signs license tokens and manages devices for PDF Reader Pro.
// Deploy:  supabase functions deploy license
// Secret:  supabase secrets set LICENSE_PRIVATE_KEY="$(cat reader-private.pem)"   (PKCS#8 PEM, ECDSA P-256; never goes in the repo)
// The caller must be signed in (Authorization: Bearer <user access token>); the user id comes from that token, never from the body.
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const MAX_DEVICES = 2;
const TRIAL_DAYS = 15;
const OFFLINE_GRACE_DAYS = 7;

const cors = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { ...cors, "Content-Type": "application/json" } });

const b64url = (data: Uint8Array) =>
  btoa(String.fromCharCode(...data)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

async function importPrivateKey(pem: string): Promise<CryptoKey> {
  const body = pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
  const der = Uint8Array.from(atob(body), (c) => c.charCodeAt(0));
  return await crypto.subtle.importKey("pkcs8", der, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
}

// WebCrypto returns the signature as r||s (IEEE P1363), which is what the app verifies.
async function sign(payload: Record<string, unknown>): Promise<string> {
  const key = await importPrivateKey(Deno.env.get("LICENSE_PRIVATE_KEY")!);
  const body = new TextEncoder().encode(JSON.stringify(payload));
  const signature = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, body));
  return `${b64url(body)}.${b64url(signature)}`;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: cors });
  try {
    const url = Deno.env.get("SUPABASE_URL")!;
    const admin = createClient(url, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!);
    const jwt = (req.headers.get("Authorization") ?? "").replace(/^Bearer\s+/i, "");
    const { data: auth, error: authError } = await admin.auth.getUser(jwt);
    if (authError || !auth.user) return json({ error: "invalid_credentials", message: "Sign in again." }, 401);
    const uid = auth.user.id;

    const body = await req.json();
    const machineId = String(body.machine_id ?? "");

    if (body.action === "devices") {
      const { data, error } = await admin.rpc("list_devices", { p_user: uid, p_machine_id: machineId });
      if (error) throw error;
      return json({ devices: data });
    }

    if (body.action === "remove") {
      const { error } = await admin.rpc("remove_device", { p_user: uid, p_device_id: String(body.device_id ?? "") });
      if (error) throw error;
      return json({ ok: true });
    }

    if (body.action === "claim") {
      if (machineId.length < 10) return json({ error: "SERVER", message: "Missing machine id." }, 400);
      const { data, error } = await admin.rpc("claim_license", {
        p_user: uid, p_machine_id: machineId, p_device_name: String(body.device_name ?? ""),
        p_max_devices: MAX_DEVICES, p_trial_days: TRIAL_DAYS,
      });
      if (error) {
        const code = (error.message ?? "").match(/DEVICE_LIMIT|BLOCKED|ACCOUNT_NOT_FOUND/)?.[0];
        if (code === "DEVICE_LIMIT") {
          const { data: devices } = await admin.rpc("list_devices", { p_user: uid, p_machine_id: machineId });
          return json({ error: code, devices }, 409);
        }
        if (code) return json({ error: code }, 403);
        throw error;
      }
      const now = Math.floor(Date.now() / 1000);
      const token = await sign({
        v: 1, product: String(body.product ?? "reader"), uid, email: data.email, mid: machineId,
        kind: data.kind, until: data.until, iat: now, valid: now + OFFLINE_GRACE_DAYS * 86400,
      });
      return json({ token });
    }

    return json({ error: "SERVER", message: "Unknown action." }, 400);
  } catch (e) {
    console.error(e);
    return json({ error: "SERVER", message: "Server error." }, 500);
  }
});
