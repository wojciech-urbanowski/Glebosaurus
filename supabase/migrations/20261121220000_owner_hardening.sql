-- Glebosaurus, faza 3 – poprawki po przeglądzie bezpieczeństwa (2026-11-21; docs/ARCHITEKTURA.md, zasady 4, 5 i 13):
-- * parowanie zdalne: jedno czekające parowanie na czujnik (nie dwa agenty na jeden klucz), ponowna próba po wyniku
--   tworzy nowe polecenie (dotąd przez minutę wracało stare – strona pokazywała jego wynik), zapisany klucz parowania
--   znika, gdy czujnik zostaje dodany, usunięty albo dostaje nowy klucz, i właściciel może go porzucić (inny czujnik pod
--   tym samym id po nieudanej próbie);
-- * właściciel strony musi mieć potwierdzony TOTP, zanim dostanie dostęp (bez tego ktoś z samym hasłem dodałby własny
--   drugi składnik przy pierwszym logowaniu);
-- * archiwum maili: tylko wysłany mail, ograniczone długości kluczy i adresów.

-- ---------------------------------------------------------------------------------------------------------------------
-- Właściciel tylko z TOTP
-- ---------------------------------------------------------------------------------------------------------------------

drop function glebosaurus.admin_owner_add(text);

-- Konto musi być w Supabase Auth i mieć potwierdzony TOTP (dodaje go strona przy pierwszym logowaniu – jeszcze przed
-- nadaniem właściciela). p_without_totp – tylko dla testów na bazie próbnej.
create function glebosaurus.admin_owner_add(p_email text, p_without_totp boolean default false) returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_user  uuid;
    v_email text;
begin
    select u.id, u.email into v_user, v_email
      from auth.users u
     where lower(u.email) = lower(trim(p_email)) and u.deleted_at is null
     order by u.created_at limit 1;
    if v_user is null then
        perform glebosaurus.fail('22023', 'w Supabase Auth nie ma konta ' || coalesce(trim(p_email), '') ||
                                          ' – załóż je w panelu Supabase (Authentication → Add user)');
    end if;
    if p_without_totp and not exists (select 1 from glebosaurus.environment where kind = 'test') then
        perform glebosaurus.fail('22023', 'właściciel bez TOTP – tylko w bazie próbnej');
    end if;
    if not p_without_totp and not exists (select 1 from auth.mfa_factors f
                                           where f.user_id = v_user and f.factor_type::text = 'totp' and f.status::text = 'verified') then
        perform glebosaurus.fail('22023', 'konto ' || v_email || ' nie ma jeszcze TOTP – zaloguj się nim na stronie (doda TOTP), ' ||
                                          'potem nadaj właściciela');
    end if;
    insert into glebosaurus.owners (user_id, email) values (v_user, v_email)
        on conflict (user_id) do update set email = excluded.email;
    return jsonb_build_object('userId', v_user, 'email', v_email);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Parowanie zdalne
-- ---------------------------------------------------------------------------------------------------------------------

-- Klucz parowania należy do jednej próby z jednym czujnikiem: gdy czujnik o tym id powstaje, znika albo dostaje nowy klucz
-- (MAC), zapisany klucz parowania jest już nieaktualny. pair_complete czyta klucz wcześniej, więc mu to nie przeszkadza.
create function glebosaurus.forget_pairing_key() returns trigger
language plpgsql set search_path = '' as $$
begin
    delete from vault.secrets where name = 'glebosaurus:pairing:' || coalesce(new.id, old.id);
    return null;
end
$$;
create trigger sensors_forget_pairing_key after insert or delete or update of mac on glebosaurus.sensors
    for each row execute function glebosaurus.forget_pairing_key();

-- Jak w 20261121200000_owner_pairing.sql; zmiany: jedno czekające parowanie na czujnik i na agenta, nowa próba po wyniku
-- to nowe polecenie (bez „ostatniej minuty” z command_create).
create or replace function public.owner_pair_start(p_agent text, p_sensor text, p_name text, p_mac text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent   glebosaurus.agents;
    v_mac     text := nullif(upper(trim(coalesce(p_mac, ''))), '');
    v_now     timestamptz;
    v_pending glebosaurus.commands;
    v_id      bigint;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    select * into v_agent from glebosaurus.agents where name = p_agent;
    if not found then perform glebosaurus.fail('22023', 'nie ma agenta ' || coalesce(p_agent, '∅')); end if;
    if not v_agent.enabled or v_agent.token_hash is null then
        perform glebosaurus.fail('22023', 'agent ' || p_agent || ' jest wyłączony albo niezarejestrowany');
    end if;
    if p_sensor is null or p_sensor !~ '^[a-z0-9][a-z0-9_-]{0,39}$' then
        perform glebosaurus.fail('22023', 'czujnik: id – małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)');
    end if;
    if exists (select 1 from glebosaurus.sensors where id = p_sensor and archived_at is not null) then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor || ' jest w archiwum – przywróć go albo wybierz inne id');
    end if;
    if v_mac is not null and v_mac !~ '^[0-9A-F]{2}([:-]?[0-9A-F]{2}){5}$' then
        perform glebosaurus.fail('22023', 'MAC: np. DC:23:51:CB:F7:7D');
    end if;
    perform glebosaurus.sensor_name(p_name, p_sensor);

    perform pg_advisory_xact_lock(hashtext('glebosaurus:pair'));
    select * into v_pending from glebosaurus.commands
     where kind = 'pair' and done_at is null and expires_at > v_now
       and (params->>'sensor' = p_sensor or target_agent_id = v_agent.id)
     order by id desc limit 1;
    if found then
        if v_pending.params->>'sensor' = p_sensor and v_pending.target_agent_id = v_agent.id then
            return jsonb_build_object('id', v_pending.id, 'created', false);
        end if;
        if v_pending.params->>'sensor' = p_sensor then
            perform glebosaurus.fail('22023', 'czujnik ' || p_sensor || ' paruje już inny agent – poczekaj na wynik (najwyżej 15 min)');
        end if;
        perform glebosaurus.fail('22023', 'agent ' || p_agent || ' paruje teraz czujnik ' || coalesce(v_pending.params->>'sensor', '?') ||
                                          ' – poczekaj na wynik (najwyżej 15 min)');
    end if;

    insert into glebosaurus.commands (kind, sensor_id, target_agent_id, params, created_at, created_by, expires_at)
    values ('pair', null, v_agent.id,
            jsonb_strip_nulls(jsonb_build_object('sensor', p_sensor, 'name', nullif(trim(coalesce(p_name, '')), ''), 'mac', v_mac)),
            v_now, 'owner-web', v_now + glebosaurus.command_ttl('pair'))
    returning id into v_id;
    return jsonb_build_object('id', v_id, 'created', true);
end
$$;

-- Porzucenie zapisanego klucza parowania (np. po nieudanej próbie, gdy pod tym id ma być inny czujnik). Nie w trakcie
-- parowania tego czujnika.
create function public.owner_pair_forget(p_sensor text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_count int;
begin
    perform glebosaurus.require_owner();
    if exists (select 1 from glebosaurus.commands where kind = 'pair' and done_at is null and expires_at > glebosaurus.clock()
                and params->>'sensor' = p_sensor) then
        perform glebosaurus.fail('22023', 'czujnik ' || coalesce(p_sensor, '∅') || ' jest właśnie parowany – poczekaj na wynik');
    end if;
    delete from vault.secrets where name = 'glebosaurus:pairing:' || p_sensor;
    get diagnostics v_count = row_count;
    return jsonb_build_object('forgotten', v_count > 0);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Archiwum maili: tylko wysłany mail, krótkie klucze i adresy
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261121180000_owner_alerts.sql; zmiany: mail musi być odnotowany jako wysłany, każdy klucz do 200 znaków, adresy
-- do 254 znaków (najwyżej 50).
create or replace function public.mail_archive_put(p_token text, p_epoch bigint, p_keys text[], p_subject text, p_recipients text[],
    p_html_gzip text) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_now   timestamptz := glebosaurus.clock();
    v_html  bytea;
    v_kind  text;
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    if p_keys is null or cardinality(p_keys) not between 1 and 50 or exists (select 1 from unnest(p_keys) k where k is null or length(k) > 200) then
        perform glebosaurus.fail('22023', 'archiwum: podaj klucze maila (1–50, każdy do 200 znaków)');
    end if;
    if p_recipients is not null and (cardinality(p_recipients) > 50
                                     or exists (select 1 from unnest(p_recipients) r where r is null or length(r) > 254)) then
        perform glebosaurus.fail('22023', 'archiwum: najwyżej 50 adresów po 254 znaki');
    end if;
    v_kind := split_part(p_keys[1], '|', 1);
    if v_kind not in ('report', 'report-request', 'alert', 'mail-test', 'agent-silent') then
        perform glebosaurus.fail('22023', 'archiwum: ten rodzaj maila nie trafia do archiwum');
    end if;
    if not exists (select 1 from glebosaurus.mail_outbox where key = p_keys[1] and agent_id = v_agent.id and sent_at is not null) then
        perform glebosaurus.fail('22023', 'archiwum: mail nie był wysłany przez tego agenta');
    end if;
    begin
        v_html := decode(p_html_gzip, 'base64');
    exception when others then
        perform glebosaurus.fail('22023', 'archiwum: treść musi być w base64');
    end;
    if length(v_html) > 1048576 or length(v_html) < 18 or substr(v_html, 1, 2) <> '\x1f8b'::bytea then
        perform glebosaurus.fail('22023', 'archiwum: treść musi być spakowana gzipem (najwyżej 1 MB)');
    end if;
    insert into glebosaurus.mail_archive (key, keys, kind, subject, recipients, agent_id, sent_at, html_gzip, size_bytes)
    values (p_keys[1], p_keys, v_kind, left(coalesce(nullif(p_subject, ''), '(bez tematu)'), 300),
            coalesce(p_recipients, '{}'), v_agent.id, v_now, v_html, length(v_html))
    on conflict (key) do nothing;
    if not found then return false; end if;
    delete from glebosaurus.mail_archive where sent_at < v_now - interval '90 days';
    return true;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.owner_pair_forget(text) from public, anon, authenticated, service_role;
grant execute on function public.owner_pair_forget(text) to authenticated;
comment on function public.owner_pair_forget(text) is 'glebosaurus-api';
