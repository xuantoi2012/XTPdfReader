-- PDF Reader Pro license backend (its own Supabase project). Paste into SQL Editor > New query > Run.
-- Accounts = Supabase Auth users. Trial 15 days from the first sign-up; paid time is set by hand (see README) by filling profiles.paid_until.
-- All reads/writes by the app go through the "license" Edge Function, which calls these functions with the service role.

create table if not exists profiles (
  id uuid primary key references auth.users(id) on delete cascade,
  email text not null,
  display_name text not null default '',
  status text not null default 'Active',            -- Active | Suspended
  trial_started date not null default current_date,
  paid_until date,                                  -- null = never bought; a date = paid license ends that day (inclusive)
  note text,
  created_at timestamptz not null default now()
);

create table if not exists devices (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references profiles(id) on delete cascade,
  machine_id text not null,
  name text not null default 'PC',
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  unique (user_id, machine_id)
);

-- A PC that already used a trial keeps its trial clock, so a second account on the same PC does not start another 15 days.
create table if not exists machine_trials (
  machine_id text primary key,
  started date not null default current_date
);

alter table profiles enable row level security;
alter table devices enable row level security;
alter table machine_trials enable row level security;
-- No policies on purpose: the anon/authenticated keys can read and write nothing. Only the Edge Function (service role) touches these tables.

create or replace function handle_new_user() returns trigger language plpgsql security definer set search_path = public as $$
begin
  insert into profiles (id, email, display_name)
  values (new.id, coalesce(new.email, ''), coalesce(new.raw_user_meta_data ->> 'display_name', ''))
  on conflict (id) do nothing;
  return new;
end $$;
drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users for each row execute function handle_new_user();

-- Registers/refreshes this PC and says what the license is. Raises DEVICE_LIMIT when two OTHER PCs are already registered, BLOCKED when suspended.
create or replace function claim_license(p_user uuid, p_machine_id text, p_device_name text, p_max_devices int default 2, p_trial_days int default 15)
returns json language plpgsql security definer set search_path = public as $$
declare
  p profiles;
  known boolean;
  other_count int;
  trial_start date;
  kind text;
  until_date date;
begin
  select * into p from profiles where id = p_user;
  if p is null then raise exception 'ACCOUNT_NOT_FOUND'; end if;
  if p.status = 'Suspended' then raise exception 'BLOCKED'; end if;

  select exists(select 1 from devices where user_id = p_user and machine_id = p_machine_id) into known;
  if not known then
    select count(*) into other_count from devices where user_id = p_user;
    if other_count >= p_max_devices then raise exception 'DEVICE_LIMIT'; end if;
    insert into devices (user_id, machine_id, name) values (p_user, p_machine_id, left(coalesce(nullif(p_device_name, ''), 'PC'), 60));
  else
    update devices set last_seen = now(), name = left(coalesce(nullif(p_device_name, ''), name), 60) where user_id = p_user and machine_id = p_machine_id;
  end if;

  insert into machine_trials (machine_id) values (p_machine_id) on conflict do nothing;
  select least(p.trial_started, started) into trial_start from machine_trials where machine_id = p_machine_id;

  if p.paid_until is not null then
    kind := 'paid'; until_date := p.paid_until;
  else
    kind := 'trial'; until_date := trial_start + p_trial_days;
  end if;
  return json_build_object('kind', kind, 'until', to_char(until_date, 'YYYY-MM-DD'), 'email', p.email);
end $$;

create or replace function list_devices(p_user uuid, p_machine_id text)
returns json language sql security definer set search_path = public as $$
  select coalesce(json_agg(json_build_object('id', id, 'name', name, 'last_seen', last_seen, 'this_pc', machine_id = p_machine_id) order by first_seen), '[]'::json)
  from devices where user_id = p_user;
$$;

create or replace function remove_device(p_user uuid, p_device_id uuid) returns void language sql security definer set search_path = public as $$
  delete from devices where id = p_device_id and user_id = p_user;
$$;

revoke all on function claim_license(uuid, text, text, int, int) from public, anon, authenticated;
revoke all on function list_devices(uuid, text) from public, anon, authenticated;
revoke all on function remove_device(uuid, uuid) from public, anon, authenticated;
grant execute on function claim_license(uuid, text, text, int, int) to service_role;
grant execute on function list_devices(uuid, text) to service_role;
grant execute on function remove_device(uuid, uuid) to service_role;
