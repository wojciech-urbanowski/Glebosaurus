-- Glebosaurus, faza 2.1.2 (dokończenie): wspólny stan alertów, polecenia i dzienne logi agentów.
-- Projekt: docs/ARCHITEKTURA.md, zasady 2, 3 i 13. Te same reguły co w rdzeniu: tabele w schemacie glebosaurus z RLS,
-- agenci tylko przez funkcje public.* (anon), zapis stanu poczty tylko przez obecnego nadawcę (blokada z epoką).

-- ---------------------------------------------------------------------------------------------------------------------
-- Tabele
-- ---------------------------------------------------------------------------------------------------------------------

-- Alerty – wspólne dla wszystkich nadawców, żeby nowy nadawca zaczynał tam, gdzie skończył poprzedni. Jeden otwarty
-- alert na czujnik i rodzaj (jak w lokalnym SQLite); początek epizodu (opened_at) liczy baza i z niego powstaje klucz
-- maila „alert|<czujnik>|<rodzaj>|<początek>”.
create table glebosaurus.alerts (
    id               bigint generated always as identity primary key,
    sensor_id        text not null references glebosaurus.sensors on delete cascade on update cascade,
    kind             text not null check (kind in ('NeedsWater', 'LowBattery', 'SensorSilent')),
    detail           text not null check (length(detail) between 1 and 500),
    opened_at        timestamptz not null,
    opened_by        int references glebosaurus.agents on delete set null,
    last_notified_at timestamptz,
    resolved_at      timestamptz
);
create unique index alerts_open_unique on glebosaurus.alerts (sensor_id, kind) where resolved_at is null;

-- Polecenia (odczyt i raport na żądanie, oddanie czujnika, parowanie, wysłanie logu). Bez wskazanego agenta trafiają
-- tam, gdzie jest władza: odczyt i oddanie – do właściciela dzierżawy czujnika, raport – do obecnego nadawcy.
create table glebosaurus.commands (
    id              bigint generated always as identity primary key,
    kind            text not null check (kind in ('read_now', 'report_now', 'release', 'pair', 'send_log')),
    sensor_id       text references glebosaurus.sensors on delete cascade on update cascade,
    target_agent_id int references glebosaurus.agents on delete cascade,
    params          jsonb not null default '{}' check (jsonb_typeof(params) = 'object' and length(params::text) <= 2000),
    created_at      timestamptz not null,
    created_by      text not null,
    expires_at      timestamptz not null,
    taken_by        int references glebosaurus.agents on delete set null,
    taken_at        timestamptz,
    done_at         timestamptz,
    result          jsonb check (length(result::text) <= 4000)
);
create index commands_pending on glebosaurus.commands (expires_at) where done_at is null;

-- Dzienne logi agentów (spakowane gzipem przez agenta) – nadawca wysyła z nich jeden log administratora z załącznikami
-- wszystkich agentów. Tabela zamiast Storage: Storage wymaga zalogowanego użytkownika albo klucza tajnego, a tak agent
-- wysyła log tą samą drogą co resztę (funkcja API z tokenem). 7 dni (settings.log_retention_days).
create table glebosaurus.agent_logs (
    agent_id     int not null references glebosaurus.agents on delete cascade,
    day          date not null,
    content_gzip bytea not null,
    size_bytes   int not null,
    uploaded_at  timestamptz not null,
    primary key (agent_id, day)
);

alter table glebosaurus.alerts     enable row level security;
alter table glebosaurus.commands   enable row level security;
alter table glebosaurus.agent_logs enable row level security;

-- ---------------------------------------------------------------------------------------------------------------------
-- Funkcje pomocnicze
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.require_notifier(p_agent glebosaurus.agents, p_epoch bigint) returns void
language plpgsql set search_path = '' as $$
begin
    if not glebosaurus.holds_notifier(p_agent, p_epoch) then
        perform glebosaurus.fail('42501', 'to może tylko obecny nadawca (blokada z aktualną epoką)');
    end if;
end
$$;

-- Dzisiejsza data w strefie czasowej raportów (settings.time_zone).
create function glebosaurus.local_today() returns date
language sql stable set search_path = '' as $$
    select (glebosaurus.clock() at time zone (select time_zone from glebosaurus.settings))::date
$$;

create function glebosaurus.alert_json(p_alert glebosaurus.alerts) returns jsonb
language sql stable set search_path = '' as $$
    select jsonb_build_object('id', p_alert.id, 'sensor', p_alert.sensor_id, 'kind', p_alert.kind,
        'detail', p_alert.detail, 'openedAt', p_alert.opened_at, 'lastNotifiedAt', p_alert.last_notified_at,
        'resolvedAt', p_alert.resolved_at)
$$;

create function glebosaurus.command_ttl(p_kind text) returns interval
language sql immutable set search_path = '' as $$
    select case p_kind when 'send_log' then interval '1 hour' when 'pair' then interval '15 minutes'
                       else interval '10 minutes' end
$$;

-- Tworzy polecenie albo zwraca istniejące: to samo polecenie (rodzaj, czujnik, agent) czekające albo utworzone w
-- ostatniej minucie nie powstaje drugi raz – „najwyżej raz na minutę na czujnik”, jak w lokalnym panelu.
create function glebosaurus.command_create(p_kind text, p_sensor_id text, p_target_agent_id int, p_params jsonb,
    p_created_by text) returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_now      timestamptz := glebosaurus.clock();
    v_existing glebosaurus.commands;
    v_id       bigint;
begin
    if p_kind is null or p_kind not in ('read_now', 'report_now', 'release', 'pair', 'send_log') then
        perform glebosaurus.fail('22023', 'nieznany rodzaj polecenia ' || coalesce(p_kind, '∅'));
    end if;
    if p_kind in ('read_now', 'release') and p_sensor_id is null then
        perform glebosaurus.fail('22023', p_kind || ' wymaga czujnika');
    end if;
    if p_sensor_id is not null and not exists (select 1 from glebosaurus.sensors where id = p_sensor_id) then
        perform glebosaurus.fail('22023', 'nie ma czujnika ' || p_sensor_id);
    end if;
    if p_kind in ('pair', 'send_log') and p_target_agent_id is null then
        perform glebosaurus.fail('22023', p_kind || ' wymaga wskazania agenta');
    end if;

    -- Blokada na czas sprawdzenia i wstawienia – dwa równoczesne zlecenia nie utworzą dwóch poleceń.
    perform pg_advisory_xact_lock(hashtext('glebosaurus:command:' || p_kind || ':' || coalesce(p_sensor_id, '')
                                           || ':' || coalesce(p_target_agent_id::text, '')));
    select * into v_existing from glebosaurus.commands
     where kind = p_kind and sensor_id is not distinct from p_sensor_id
       and target_agent_id is not distinct from p_target_agent_id
       and ((done_at is null and expires_at > v_now) or created_at > v_now - interval '1 minute')
     order by id desc limit 1;
    if found then
        return jsonb_build_object('id', v_existing.id, 'created', false);
    end if;

    insert into glebosaurus.commands (kind, sensor_id, target_agent_id, params, created_at, created_by, expires_at)
    values (p_kind, p_sensor_id, p_target_agent_id, coalesce(p_params, '{}'), v_now, p_created_by,
            v_now + glebosaurus.command_ttl(p_kind))
    returning id into v_id;
    delete from glebosaurus.commands where created_at < v_now - interval '30 days';
    return jsonb_build_object('id', v_id, 'created', true);
end
$$;

-- Polecenia skierowane teraz do agenta (po dzierżawach – liczy się świeży stan). Oznacza je jako odebrane; dopóki nie są
-- wykonane ani nie wygasły, wracają w każdej synchronizacji (agent rozpoznaje je po id).
create function glebosaurus.commands_for(p_agent glebosaurus.agents, p_now timestamptz) returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_commands jsonb;
begin
    select coalesce(jsonb_agg(jsonb_build_object('id', c.id, 'kind', c.kind, 'sensor', c.sensor_id, 'params', c.params,
                                                 'createdAt', c.created_at,
                                                 'expiresInSeconds', glebosaurus.seconds(c.expires_at - p_now))
                              order by c.id), '[]'::jsonb)
      into v_commands
      from (select * from glebosaurus.commands c
             where c.done_at is null and c.expires_at > p_now
               and (c.target_agent_id = p_agent.id
                    or (c.target_agent_id is null and c.kind in ('read_now', 'release') and exists (
                            select 1 from glebosaurus.leases l
                             where l.resource = 'sensor:' || c.sensor_id and l.holder_id = p_agent.id and l.expires_at > p_now))
                    or (c.target_agent_id is null and c.kind = 'report_now' and exists (
                            select 1 from glebosaurus.leases l
                             where l.resource = 'notifier' and l.holder_id = p_agent.id and l.expires_at > p_now)))
             order by c.id limit 50) c;

    update glebosaurus.commands set taken_by = p_agent.id, taken_at = p_now
     where id in (select (e->>'id')::bigint from jsonb_array_elements(v_commands) e)
       and taken_by is distinct from p_agent.id;
    return v_commands;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Administracja
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.admin_command(p_kind text, p_sensor_id text default null, p_agent_name text default null,
    p_params jsonb default '{}') returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_agent_id int;
begin
    if p_agent_name is not null then
        select id into v_agent_id from glebosaurus.agents where name = p_agent_name;
        if v_agent_id is null then perform glebosaurus.fail('22023', 'nie ma agenta ' || p_agent_name); end if;
    end if;
    return glebosaurus.command_create(p_kind, p_sensor_id, v_agent_id, p_params, 'owner');
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API agentów
-- ---------------------------------------------------------------------------------------------------------------------

-- Otwarte alerty – czytać może każdy agent (np. lokalny panel), zmieniać tylko nadawca.
create function public.alert_list(p_token text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.agent_auth(p_token);
    return coalesce((select jsonb_agg(glebosaurus.alert_json(a) order by a.id)
                       from glebosaurus.alerts a where a.resolved_at is null), '[]'::jsonb);
end
$$;

-- Otwiera alert albo zwraca już otwarty (created = false) – powtórzenie po restarcie nie tworzy drugiego epizodu.
create function public.alert_open(p_token text, p_epoch bigint, p_sensor text, p_kind text, p_detail text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_alert glebosaurus.alerts;
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    if p_kind is null or p_kind not in ('NeedsWater', 'LowBattery', 'SensorSilent') then
        perform glebosaurus.fail('22023', 'nieznany rodzaj alertu ' || coalesce(p_kind, '∅'));
    end if;
    if not exists (select 1 from glebosaurus.sensors where id = p_sensor) then
        perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_sensor, '∅'));
    end if;
    if p_detail is null or length(p_detail) not between 1 and 500 then
        perform glebosaurus.fail('22023', 'opis alertu musi mieć od 1 do 500 znaków');
    end if;
    insert into glebosaurus.alerts (sensor_id, kind, detail, opened_at, opened_by)
    values (p_sensor, p_kind, p_detail, glebosaurus.clock(), v_agent.id)
    on conflict (sensor_id, kind) where resolved_at is null do nothing
    returning * into v_alert;
    if found then
        return jsonb_build_object('created', true, 'alert', glebosaurus.alert_json(v_alert));
    end if;
    select * into v_alert from glebosaurus.alerts where sensor_id = p_sensor and kind = p_kind and resolved_at is null;
    return jsonb_build_object('created', false, 'alert', glebosaurus.alert_json(v_alert));
end
$$;

create function public.alert_resolve(p_token text, p_epoch bigint, p_id bigint) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    update glebosaurus.alerts set resolved_at = glebosaurus.clock() where id = p_id and resolved_at is null;
    return found;
end
$$;

-- Po wysłaniu maila o alertach (albo przypomnienia) – zwraca liczbę oznaczonych.
create function public.alert_notified(p_token text, p_epoch bigint, p_ids bigint[]) returns int
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_count int;
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    update glebosaurus.alerts set last_notified_at = glebosaurus.clock()
     where id = any(p_ids) and resolved_at is null;
    get diagnostics v_count = row_count;
    return v_count;
end
$$;

-- Agent (lokalny panel) może zlecić odczyt albo raport na żądanie; reszta poleceń – tylko właściciel.
create function public.command_create(p_token text, p_kind text, p_sensor text default null) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    if p_kind is null or p_kind not in ('read_now', 'report_now') then
        perform glebosaurus.fail('22023', 'agent może zlecić tylko read_now i report_now');
    end if;
    return glebosaurus.command_create(p_kind, case when p_kind = 'read_now' then p_sensor end, null, '{}', v_agent.name);
end
$$;

-- Wynik polecenia. Zamknąć może agent, który je odebrał albo do którego było skierowane wprost.
create function public.command_done(p_token text, p_id bigint, p_result jsonb default null) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    if p_result is not null and length(p_result::text) > 4000 then
        perform glebosaurus.fail('22023', 'wynik polecenia może mieć najwyżej 4000 znaków');
    end if;
    update glebosaurus.commands set done_at = glebosaurus.clock(), result = p_result
     where id = p_id and done_at is null and (taken_by = v_agent.id or target_agent_id = v_agent.id);
    return found;
end
$$;

-- Dzienny log agenta: gzip w base64, najwyżej 4 MB po spakowaniu; ponowne wysłanie tego samego dnia zastępuje log.
-- Przy okazji usuwa logi starsze niż settings.log_retention_days.
create function public.log_put(p_token text, p_day date, p_content_gzip_base64 text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_today date := glebosaurus.local_today();
    v_bytes bytea;
begin
    if p_day is null or p_day > v_today + 1 or p_day < v_today - 30 then
        perform glebosaurus.fail('22023', 'dzień logu musi być z ostatnich 30 dni');
    end if;
    if p_content_gzip_base64 is null or length(p_content_gzip_base64) > 5600000 then
        perform glebosaurus.fail('22023', 'log musi być spakowany gzipem i mieć najwyżej 4 MB');
    end if;
    v_bytes := decode(p_content_gzip_base64, 'base64');
    if length(v_bytes) > 4194304 or length(v_bytes) < 18 or substring(v_bytes from 1 for 2) <> '\x1f8b'::bytea then
        perform glebosaurus.fail('22023', 'log musi być spakowany gzipem i mieć najwyżej 4 MB');
    end if;
    insert into glebosaurus.agent_logs (agent_id, day, content_gzip, size_bytes, uploaded_at)
    values (v_agent.id, p_day, v_bytes, length(v_bytes), glebosaurus.clock())
    on conflict (agent_id, day) do update
        set content_gzip = excluded.content_gzip, size_bytes = excluded.size_bytes, uploaded_at = excluded.uploaded_at;
    delete from glebosaurus.agent_logs
     where day < v_today - (select log_retention_days from glebosaurus.settings);
    return jsonb_build_object('day', p_day, 'sizeBytes', length(v_bytes));
end
$$;

-- Logi wszystkich agentów z danego dnia – tylko dla obecnego nadawcy (log administratora).
create function public.log_get(p_token text, p_epoch bigint, p_day date) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    return coalesce((
        select jsonb_agg(jsonb_build_object('agent', a.name, 'day', l.day, 'sizeBytes', l.size_bytes,
                                            'uploadedAt', l.uploaded_at,
                                            'contentGzipBase64', translate(encode(l.content_gzip, 'base64'), E'\n', ''))
                         order by a.name)
          from glebosaurus.agent_logs l join glebosaurus.agents a on a.id = l.agent_id
         where l.day = p_day), '[]'::jsonb);
end
$$;

-- agent_sync z rdzenia, rozszerzony o polecenia skierowane do agenta („commands” w odpowiedzi).
create or replace function public.agent_sync(p_token text, p_payload jsonb) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent     glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_now       timestamptz := glebosaurus.clock();
    v_readings  jsonb := coalesce(p_payload->'readings', '[]');
    v_sightings jsonb := coalesce(p_payload->'sightings', '[]');
    v_releases  jsonb := coalesce(p_payload->'releases', '[]');
    v_claims    jsonb := coalesce(p_payload->'claims', '[]');
    v_received  int;
    v_inserted  int;
    v_rejected  jsonb;
    v_released  jsonb := '[]';
    v_leases    jsonb := '[]';
    v_commands  jsonb;
    v_item      jsonb;
    v_resource  text;
begin
    if p_payload is null or jsonb_typeof(p_payload) <> 'object' or (p_payload->>'protocol') is distinct from '1' then
        perform glebosaurus.fail('22023', 'nieobsługiwana wersja protokołu (oczekiwana 1)');
    end if;
    if jsonb_typeof(v_readings) <> 'array' or jsonb_array_length(v_readings) > 1000
       or jsonb_typeof(v_sightings) <> 'array' or jsonb_array_length(v_sightings) > 200
       or jsonb_typeof(v_releases) <> 'array' or jsonb_array_length(v_releases) > 200
       or jsonb_typeof(v_claims) <> 'array' or jsonb_array_length(v_claims) > 200 then
        perform glebosaurus.fail('22023', 'readings (≤ 1000), sightings, releases i claims (≤ 200) muszą być tablicami');
    end if;

    update glebosaurus.agents
       set last_seen_at = v_now, last_protocol = 1, last_status = p_payload->'status'
     where id = v_agent.id;

    v_received := jsonb_array_length(v_readings);
    with source as (
        select r.sensor, r.at, r.moisture, r."tempTenths" as temp_tenths, r.battery, r."batteryState" as battery_state,
               case
                   when r.sensor is null or r.at is null then 'missing'
                   when not exists (select 1 from glebosaurus.sensors s where s.id = r.sensor) then 'unknown_sensor'
                   when r.at > v_now + interval '10 minutes' then 'future'
                   when r.at < timestamptz '2020-01-01' then 'too_old'
                   when r.moisture not between 0 and 100 or r."tempTenths" not between -400 and 1000
                     or r.battery not between 0 and 100 or r."batteryState" not between 0 and 9 then 'out_of_range'
               end as reason
          from jsonb_to_recordset(v_readings) as r(sensor text, at timestamptz, moisture int, "tempTenths" int,
                                                   battery int, "batteryState" int)),
    inserted as (
        insert into glebosaurus.readings (sensor_id, at, moisture, temp_tenths, battery, battery_state, agent_id)
        select sensor, at, moisture, temp_tenths, battery, battery_state, v_agent.id from source where reason is null
        on conflict do nothing
        returning 1)
    select (select count(*) from inserted),
           coalesce((select jsonb_agg(jsonb_build_object('sensor', sensor, 'at', at, 'reason', reason))
                       from source where reason is not null), '[]'::jsonb)
      into v_inserted, v_rejected;

    insert into glebosaurus.sightings as g (agent_id, sensor_id, rssi, heard_at, last_attempt_at, last_success_at,
                                            consecutive_failures, updated_at)
    select distinct on (s.sensor)
           v_agent.id, s.sensor,
           case when s.rssi between -126 and 20 then s.rssi end,
           glebosaurus.ago(v_now, s."heardSecondsAgo"),
           glebosaurus.ago(v_now, s."lastAttemptSecondsAgo"),
           glebosaurus.ago(v_now, s."lastSuccessSecondsAgo"),
           greatest(coalesce(s.failures, 0), 0), v_now
      from jsonb_to_recordset(v_sightings) as s(sensor text, rssi int, "heardSecondsAgo" double precision,
           "lastAttemptSecondsAgo" double precision, "lastSuccessSecondsAgo" double precision, failures int)
      join glebosaurus.sensors x on x.id = s.sensor
     order by s.sensor, s."heardSecondsAgo" nulls last
    on conflict (agent_id, sensor_id) do update set
        rssi = excluded.rssi, heard_at = coalesce(excluded.heard_at, g.heard_at),
        last_attempt_at = coalesce(excluded.last_attempt_at, g.last_attempt_at),
        last_success_at = coalesce(excluded.last_success_at, g.last_success_at),
        consecutive_failures = excluded.consecutive_failures, updated_at = excluded.updated_at;

    for v_item in select value from jsonb_array_elements(v_releases) loop
        v_released := v_released || jsonb_build_object('resource', v_item->>'resource',
            'released', glebosaurus.lease_release(v_agent, v_item->>'resource', (v_item->>'epoch')::bigint));
    end loop;
    for v_resource in select distinct value from jsonb_array_elements_text(v_claims) order by 1 loop
        v_leases := v_leases || glebosaurus.lease_claim(v_agent, v_resource);
    end loop;

    v_commands := glebosaurus.commands_for(v_agent, v_now);

    return jsonb_build_object(
        'protocol', 1,
        'serverTime', v_now,
        'configVersion', (select config_version from glebosaurus.settings),
        'agent', jsonb_build_object('name', v_agent.name, 'canSendMail', v_agent.can_send_mail,
                                    'senderPriority', v_agent.sender_priority),
        'readings', jsonb_build_object('received', v_received, 'inserted', v_inserted, 'rejected', v_rejected),
        'released', v_released,
        'leases', v_leases,
        'commands', v_commands,
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'sensor', s.id,
                'holder', case when l.expires_at > v_now then ha.name end,
                'holderExpiresInSeconds', case when l.expires_at > v_now then glebosaurus.seconds(l.expires_at - v_now) end,
                'bestOther', (
                    select jsonb_build_object('agent', a2.name, 'rssi', g.rssi,
                                              'heardSecondsAgo', glebosaurus.seconds(v_now - g.heard_at))
                      from glebosaurus.sightings g join glebosaurus.agents a2 on a2.id = g.agent_id
                     where g.sensor_id = s.id and g.agent_id <> v_agent.id and g.rssi is not null
                       and g.heard_at > v_now - interval '15 minutes'
                     order by g.rssi desc, a2.name limit 1)) order by s.id)
              from glebosaurus.sensors s
              left join glebosaurus.leases l on l.resource = 'sensor:' || s.id
              left join glebosaurus.agents ha on ha.id = l.holder_id
             where s.enabled), '[]'::jsonb),
        'notifier', (
            select jsonb_build_object('holder', a.name, 'epoch', l.epoch,
                                      'expiresInSeconds', glebosaurus.seconds(l.expires_at - v_now))
              from glebosaurus.leases l join glebosaurus.agents a on a.id = l.holder_id
             where l.resource = 'notifier' and l.expires_at > v_now),
        'agents', coalesce((
            select jsonb_agg(jsonb_build_object('name', a.name, 'canSendMail', a.can_send_mail,
                                                'senderPriority', a.sender_priority,
                                                'lastSeenSecondsAgo', glebosaurus.seconds(v_now - a.last_seen_at))
                             order by a.name)
              from glebosaurus.agents a where a.enabled and a.token_hash is not null), '[]'::jsonb));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.alert_list(text) from public, anon, authenticated, service_role;
revoke all on function public.alert_open(text, bigint, text, text, text) from public, anon, authenticated, service_role;
revoke all on function public.alert_resolve(text, bigint, bigint) from public, anon, authenticated, service_role;
revoke all on function public.alert_notified(text, bigint, bigint[]) from public, anon, authenticated, service_role;
revoke all on function public.command_create(text, text, text) from public, anon, authenticated, service_role;
revoke all on function public.command_done(text, bigint, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.log_put(text, date, text) from public, anon, authenticated, service_role;
revoke all on function public.log_get(text, bigint, date) from public, anon, authenticated, service_role;

grant execute on function public.alert_list(text) to anon;
grant execute on function public.alert_open(text, bigint, text, text, text) to anon;
grant execute on function public.alert_resolve(text, bigint, bigint) to anon;
grant execute on function public.alert_notified(text, bigint, bigint[]) to anon;
grant execute on function public.command_create(text, text, text) to anon;
grant execute on function public.command_done(text, bigint, jsonb) to anon;
grant execute on function public.log_put(text, date, text) to anon;
grant execute on function public.log_get(text, bigint, date) to anon;

comment on function public.alert_list(text) is 'glebosaurus-api';
comment on function public.alert_open(text, bigint, text, text, text) is 'glebosaurus-api';
comment on function public.alert_resolve(text, bigint, bigint) is 'glebosaurus-api';
comment on function public.alert_notified(text, bigint, bigint[]) is 'glebosaurus-api';
comment on function public.command_create(text, text, text) is 'glebosaurus-api';
comment on function public.command_done(text, bigint, jsonb) is 'glebosaurus-api';
comment on function public.log_put(text, date, text) is 'glebosaurus-api';
comment on function public.log_get(text, bigint, date) is 'glebosaurus-api';
