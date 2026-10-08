# PDF Reader Pro license server (own Supabase project)

Rules (decided 2026-10-08): one account per product, 15-day trial then the app locks, up to 2 PCs per account, the virtual printer needs no license.

## One-time setup
1. Create a new Supabase project (free tier is enough). Authentication > Providers > Email: keep **Confirm email** on (it slows trial abuse).
2. SQL Editor: run `schema.sql`.
3. Install the Supabase CLI, then in this folder: `supabase link --project-ref <ref>` and `supabase functions deploy license`.
4. Set the signing key as a secret (the private key was generated at `C:\Users\condu\.xt-license\reader-private.pem`, outside the repo; keep a backup, losing it means issuing a new key pair and a new app build):
   `supabase secrets set LICENSE_PRIVATE_KEY="$(cat reader-private.pem)"`
5. Put the project URL and the **anon** key (Settings > API) into `Licensing/LicenseConfig.cs` (`SupabaseUrl`, `AnonKey`). Never the service-role key.

## Selling a year
The customer signs up in the app (the trial starts). When they pay, set the end date in SQL Editor:

```sql
update profiles set paid_until = '2027-10-31' where email = 'customer@example.com';   -- first purchase or renewal (use the new end date)
update profiles set status = 'Suspended' where email = 'customer@example.com';        -- block (refund, abuse)
delete from devices where user_id = (select id from profiles where email = 'customer@example.com');   -- free both PC slots
```

The app picks the change up within 24 hours (`RefreshAfterHours`), or at once when the customer signs out and in. A web or XTAdmin screen for this can come later.

## What stops what
- Token is signed (ECDSA P-256); the app holds only the public key, so the local file cannot be edited to extend a date.
- Token works offline 7 days, then the app must reach the server. A clock moved backwards forces an online check.
- Trial start comes from the server, and a PC that already had a trial keeps its start date for any new account.
- Private key rotation: generate a new pair, change `PublicKey` in `LicenseConfig.cs`, ship a new build, set the new secret.
