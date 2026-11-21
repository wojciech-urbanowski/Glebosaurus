-- Glebosaurus, faza 3 (etap 1) – właściciel w przeglądarce: konto Supabase Auth z TOTP, lista właścicieli i pierwsze
-- funkcje strony (docs/ARCHITEKTURA.md, zasady 4 i 10).
--
-- Bezpieczeństwo:
-- * strona loguje się do Supabase Auth (hasło + TOTP) i woła wyłącznie funkcje public.owner_* „security definer” z pustym
--   search_path, wykonywalne tylko przez rolę authenticated (nie anon);
-- * każda funkcja zaczyna od glebosaurus.require_owner(): token musi mieć aal2 (zalogowanie z TOTP), a jego użytkownik
--   być w glebosaurus.owners – samo konto w Auth (np. założone przez pomyłkę) nie daje niczego; tabele zostają zamknięte;
-- * właścicieli dodaje i usuwa tylko połączenie z bazą (glebosaurus.admin_owner_*, polecenie cloud owner).
--
-- Bez klucza obcego do auth.users: schemat auth należy do Supabase, a kopia zapasowa i testy nie powinny od niego
-- zależeć. Usunięte konto nie zaloguje się ponownie, a jego ostatni token wygasa sam (domyślnie po godzinie);
-- natychmiast odcina cloud owner --remove.

create table glebosaurus.owners (
    user_id  uuid primary key,
    email    text not null check (length(email) between 3 and 254),
    added_at timestamptz not null default now()
);
alter table glebosaurus.owners enable row level security;

-- Zalogowany właściciel z drugim składnikiem albo SQLSTATE 42501 (Data API: 403) z powodem w treści:
-- „mfa_required” – token bez aal2 (strona prosi o kod TOTP), „not_owner” – konto spoza listy właścicieli.
create function glebosaurus.require_owner() returns uuid
language plpgsql stable set search_path = '' as $$
declare
    v_claims jsonb := coalesce(nullif(current_setting('request.jwt.claims', true), ''), '{}')::jsonb;
    v_user   uuid;
begin
    if coalesce(v_claims->>'aal', '') <> 'aal2' then
        perform glebosaurus.fail('42501', 'mfa_required');
    end if;
    begin
        v_user := nullif(v_claims->>'sub', '')::uuid;
    exception when invalid_text_representation then
        v_user := null;
    end;
    if v_user is null or not exists (select 1 from glebosaurus.owners where user_id = v_user) then
        perform glebosaurus.fail('42501', 'not_owner');
    end if;
    return v_user;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Administracja (połączenie z bazą): właściciele
-- ---------------------------------------------------------------------------------------------------------------------

-- Konto musi już istnieć w Supabase Auth (Authentication → Add user); wielkość liter adresu nie ma znaczenia.
create function glebosaurus.admin_owner_add(p_email text) returns jsonb
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
    insert into glebosaurus.owners (user_id, email) values (v_user, v_email)
        on conflict (user_id) do update set email = excluded.email;
    return jsonb_build_object('userId', v_user, 'email', v_email);
end
$$;

create function glebosaurus.admin_owner_remove(p_email text) returns boolean
language plpgsql set search_path = '' as $$
declare
    v_count int;
begin
    delete from glebosaurus.owners where lower(email) = lower(trim(p_email));
    get diagnostics v_count = row_count;
    return v_count > 0;
end
$$;

-- Lista właścicieli z tym, co o koncie wie Auth (czy istnieje, czy ma TOTP) – bez żadnych sekretów.
create function glebosaurus.admin_owner_list() returns jsonb
language sql stable set search_path = '' as $$
    select coalesce(jsonb_agg(jsonb_build_object(
               'email', o.email, 'userId', o.user_id, 'addedAt', o.added_at,
               'account', u.id is not null and u.deleted_at is null,
               'totp', exists (select 1 from auth.mfa_factors f
                                where f.user_id = o.user_id and f.factor_type::text = 'totp' and f.status::text = 'verified'))
               order by o.email), '[]'::jsonb)
      from glebosaurus.owners o
      left join auth.users u on u.id = o.user_id
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony właściciela (public, Data API: POST /rest/v1/rpc/<nazwa> z tokenem Supabase Auth)
-- ---------------------------------------------------------------------------------------------------------------------

-- Kim jestem i jaka jest baza: strona porównuje listę migracji z tą, której potrzebuje, i mówi, czego brakuje.
create function public.owner_info() returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_user uuid := glebosaurus.require_owner();
begin
    return jsonb_build_object(
        'email', (select email from glebosaurus.owners where user_id = v_user),
        'serverTime', glebosaurus.clock(),
        'test', exists (select 1 from glebosaurus.environment where kind = 'test'),
        'migrations', coalesce((select jsonb_agg(version order by version) from glebosaurus.schema_migrations), '[]'::jsonb));
end
$$;

-- Pulpit: czujniki z ostatnim odczytem i tym, kto je czyta, agenci z ostatnim sygnałem życia, obecny nadawca.
-- Czujniki wyłączone też (strona pokazuje je osobno); sekretów tu nie ma.
create function public.owner_dashboard() returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    return jsonb_build_object(
        'serverTime', v_now,
        'configVersion', (select config_version from glebosaurus.settings),
        'timeZone', (select time_zone from glebosaurus.settings),
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled,
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'lowBatteryPercent', s.low_battery_percent,
                'last', (select jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', r.temp_tenths / 10.0,
                                                   'battery', r.battery, 'batteryState', r.battery_state, 'agent', ra.name)
                           from glebosaurus.readings r left join glebosaurus.agents ra on ra.id = r.agent_id
                          where r.sensor_id = s.id order by r.at desc limit 1),
                'holder', case when l.expires_at > v_now then ha.name end) order by s.name, s.id)
              from glebosaurus.sensors s
              left join glebosaurus.leases l on l.resource = 'sensor:' || s.id
              left join glebosaurus.agents ha on ha.id = l.holder_id), '[]'::jsonb),
        'notifier', (
            select jsonb_build_object('agent', a.name, 'expiresAt', l.expires_at)
              from glebosaurus.leases l join glebosaurus.agents a on a.id = l.holder_id
             where l.resource = 'notifier' and l.expires_at > v_now),
        'agents', coalesce((
            select jsonb_agg(jsonb_build_object(
                'name', a.name, 'canSendMail', a.can_send_mail, 'senderPriority', a.sender_priority,
                'enabled', a.enabled, 'enrolled', a.token_hash is not null, 'lastSeenAt', a.last_seen_at,
                'status', glebosaurus.agent_status_view(a.last_status)) order by a.name)
              from glebosaurus.agents a), '[]'::jsonb));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia: owner_* tylko dla authenticated (Supabase domyślnie daje nowym funkcjom w public execute dla anon,
-- authenticated i service_role – stąd jawne odebranie)
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.owner_info() from public, anon, authenticated, service_role;
revoke all on function public.owner_dashboard() from public, anon, authenticated, service_role;

grant execute on function public.owner_info() to authenticated;
grant execute on function public.owner_dashboard() to authenticated;

comment on function public.owner_info() is 'glebosaurus-api';
comment on function public.owner_dashboard() is 'glebosaurus-api';
