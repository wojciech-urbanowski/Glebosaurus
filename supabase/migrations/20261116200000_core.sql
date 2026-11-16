-- Glebosaurus, faza 2.1.2 – rdzeń chmury: agenci i rejestracja, konfiguracja, odczyty, słyszenia, dzierżawy (czujniki i
-- blokada nadawcy), kolejka maili bez duplikatów i sekrety w Vault. Projekt: docs/ARCHITEKTURA.md, zasady 1–4 i 10.
--
-- Bezpieczeństwo:
-- * tabele i funkcje pomocnicze leżą w schemacie glebosaurus, którego Data API nie wystawia; RLS włączone na każdej
--   tabeli bez żadnych zasad, więc nawet przypadkowy grant nic nie odsłoni;
-- * agenci (klucz publikowalny = rola anon) wołają tylko funkcje public.* „security definer” z pustym search_path,
--   a każda sprawdza token agenta (w bazie tylko jego skrót SHA-256);
-- * funkcje administracyjne (glebosaurus.admin_*) woła wyłącznie właściciel przez połączenie z bazą (hasło bazy),
--   nigdy przez Data API;
-- * Postgres daje nowym funkcjom EXECUTE dla PUBLIC – tu jest to odbierane jawnie dla każdej funkcji i domyślnie dla
--   przyszłych funkcji tworzonych przez postgres.
--
-- Czas: wszystko liczy glebosaurus.clock() = now(). Wyłącznie w bazie oznaczonej jako próbna (glebosaurus.environment)
-- testy mogą przesunąć zegar ustawieniem glebosaurus.test_now w swojej transakcji; Data API nie daje klientom ustawiać
-- takich zmiennych, a prawdziwa baza nie ma znacznika.

create schema if not exists glebosaurus;
revoke all on schema glebosaurus from public;

alter default privileges for role postgres revoke execute on functions from public;

-- ---------------------------------------------------------------------------------------------------------------------
-- Tabele
-- ---------------------------------------------------------------------------------------------------------------------

-- Znacznik bazy próbnej (jeden wiersz). Ustawiają go tylko testy na projekcie glebosaurus-test.
create table glebosaurus.environment (
    id        boolean primary key default true check (id),
    kind      text not null check (kind in ('production', 'test')),
    marked_at timestamptz not null default now()
);

create table glebosaurus.agents (
    id              int generated always as identity primary key,
    name            text not null unique check (name ~ '^[a-z0-9][a-z0-9_-]{0,39}$'),
    can_send_mail   boolean not null default false,
    -- 1 = najważniejszy kandydat na nadawcę; czeka 1 min przy wolnej blokadzie (ARCHITEKTURA.md, zasada 2).
    sender_priority int check (sender_priority between 1 and 9),
    enabled         boolean not null default true,
    token_hash      bytea unique,
    created_at      timestamptz not null default now(),
    enrolled_at     timestamptz,
    last_seen_at    timestamptz,
    last_protocol   int,
    last_status     jsonb,
    check (not can_send_mail or sender_priority is not null)
);

create table glebosaurus.enrollment_codes (
    code_hash  bytea primary key,
    agent_id   int not null references glebosaurus.agents on delete cascade,
    created_at timestamptz not null default now(),
    expires_at timestamptz not null,
    used_at    timestamptz
);

create function glebosaurus.valid_times(p_times text[]) returns boolean
language sql immutable set search_path = '' as $$
    select coalesce(bool_and(t ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'), true) from unnest(p_times) t
$$;

create function glebosaurus.valid_addresses(p_addresses text[]) returns boolean
language sql immutable set search_path = '' as $$
    select coalesce(bool_and(a ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$' and length(a) <= 254), true) from unnest(p_addresses) a
$$;

-- Ustawienia wspólne (jeden wiersz). config_version rośnie przy każdej zmianie konfiguracji – agenci widzą go w
-- agent_sync i wtedy pobierają get_config.
create table glebosaurus.settings (
    id                    boolean primary key default true check (id),
    time_zone             text not null default 'Europe/Warsaw' check (time_zone <> ''),
    read_interval_minutes numeric not null default 15 check (read_interval_minutes >= 0.5),
    scan_window_seconds   int not null default 60 check (scan_window_seconds between 5 and 600),
    report_times          text[] not null default '{07:00,19:00}' check (glebosaurus.valid_times(report_times)),
    report_catch_up_hours numeric not null default 3 check (report_catch_up_hours >= 0),
    alert_reminder_hours  numeric not null default 24 check (alert_reminder_hours > 0),
    log_retention_days    int not null default 7 check (log_retention_days between 1 and 30),
    gmail_address         text check (gmail_address is null or glebosaurus.valid_addresses(array[gmail_address])),
    alert_recipients      text[] not null default '{}' check (glebosaurus.valid_addresses(alert_recipients)),
    report_recipients     text[] not null default '{}' check (glebosaurus.valid_addresses(report_recipients)),
    admin_recipients      text[] not null default '{}' check (glebosaurus.valid_addresses(admin_recipients)),
    config_version        bigint not null default 1,
    updated_at            timestamptz not null default now()
);
insert into glebosaurus.settings default values;

create table glebosaurus.sensors (
    id                           text primary key check (id ~ '^[a-z0-9][a-z0-9_-]{0,39}$'),
    name                         text not null check (length(name) between 1 and 80),
    mac                          text not null unique check (mac ~ '^[0-9A-F]{2}(:[0-9A-F]{2}){5}$'),
    enabled                      boolean not null default true,
    read_interval_minutes        numeric check (read_interval_minutes >= 0.5),
    watering_below_percent       int not null default 25 check (watering_below_percent between 0 and 100),
    watering_clear_above_percent int not null default 35 check (watering_clear_above_percent between 0 and 100),
    consecutive_readings         int not null default 2 check (consecutive_readings between 1 and 20),
    low_battery_percent          int not null default 18 check (low_battery_percent between 0 and 100),
    silent_after_hours           numeric not null default 6 check (silent_after_hours > 0),
    created_at                   timestamptz not null default now(),
    updated_at                   timestamptz not null default now(),
    check (watering_clear_above_percent >= watering_below_percent)
);

-- Odczyty: ten sam klucz co w lokalnym SQLite, więc dosyłanie jest idempotentne.
create table glebosaurus.readings (
    sensor_id     text not null references glebosaurus.sensors on update cascade,
    at            timestamptz not null,
    moisture      smallint check (moisture between 0 and 100),
    temp_tenths   smallint check (temp_tenths between -400 and 1000),
    battery       smallint check (battery between 0 and 100),
    battery_state smallint check (battery_state between 0 and 9),
    agent_id      int references glebosaurus.agents on delete set null,
    primary key (sensor_id, at)
);

-- Słyszenia: jeden nadpisywany wiersz na parę agent–czujnik, bez historii.
create table glebosaurus.sightings (
    agent_id             int not null references glebosaurus.agents on delete cascade,
    sensor_id            text not null references glebosaurus.sensors on delete cascade on update cascade,
    rssi                 smallint,
    heard_at             timestamptz,
    last_attempt_at      timestamptz,
    last_success_at      timestamptz,
    consecutive_failures int not null default 0,
    updated_at           timestamptz not null,
    primary key (agent_id, sensor_id)
);

-- Dzierżawy: „sensor:<id>” (30 min) i „notifier” – blokada nadawcy (5 min). Wiersz zostaje po oddaniu, żeby epoka
-- tylko rosła.
create table glebosaurus.leases (
    resource   text primary key check (resource = 'notifier' or resource ~ '^sensor:[a-z0-9][a-z0-9_-]{0,39}$'),
    holder_id  int references glebosaurus.agents on delete set null,
    epoch      bigint not null default 0,
    granted_at timestamptz,
    renewed_at timestamptz,
    expires_at timestamptz
);

-- Kolejka maili: unikalny klucz to „tak” tylko raz w historii (ARCHITEKTURA.md, zasada 2).
create table glebosaurus.mail_outbox (
    key        text primary key check (key ~ '^(report|alert|log|agent-silent|report-request)\|.+$' and length(key) <= 200),
    agent_id   int references glebosaurus.agents on delete set null,
    epoch      bigint not null,
    claimed_at timestamptz not null,
    sent_at    timestamptz,
    message_id text check (length(message_id) <= 250)
);

alter table glebosaurus.environment      enable row level security;
alter table glebosaurus.agents           enable row level security;
alter table glebosaurus.enrollment_codes enable row level security;
alter table glebosaurus.settings         enable row level security;
alter table glebosaurus.sensors          enable row level security;
alter table glebosaurus.readings         enable row level security;
alter table glebosaurus.sightings        enable row level security;
alter table glebosaurus.leases           enable row level security;
alter table glebosaurus.mail_outbox      enable row level security;

-- ---------------------------------------------------------------------------------------------------------------------
-- Funkcje pomocnicze (schemat glebosaurus, bez dostępu dla agentów)
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.clock() returns timestamptz
language sql stable set search_path = '' as $$
    select case
        when exists (select 1 from glebosaurus.environment where kind = 'test')
             and nullif(current_setting('glebosaurus.test_now', true), '') is not null
        then current_setting('glebosaurus.test_now', true)::timestamptz
        else now()
    end
$$;

create function glebosaurus.fail(p_code text, p_message text) returns void
language plpgsql set search_path = '' as $$
begin
    raise exception using errcode = p_code, message = 'Glebosaurus: ' || p_message;
end
$$;

create function glebosaurus.random_token(p_prefix text, p_bytes int) returns text
language sql volatile set search_path = '' as $$
    select p_prefix || rtrim(translate(encode(extensions.gen_random_bytes(p_bytes), 'base64'), '+/', '-_'), '=')
$$;

create function glebosaurus.token_hash(p_token text) returns bytea
language sql stable set search_path = '' as $$
    select sha256(convert_to(p_token, 'UTF8'))
$$;

-- Agent po tokenie albo błąd 28000 (Data API zwraca wtedy 403 – agent wie, że token unieważniono).
create function glebosaurus.agent_auth(p_token text) returns glebosaurus.agents
language plpgsql set search_path = '' as $$
declare
    v_agent glebosaurus.agents;
begin
    if p_token is null or length(p_token) > 200 then
        perform glebosaurus.fail('28000', 'nieznany lub unieważniony token agenta');
    end if;
    select * into v_agent from glebosaurus.agents where token_hash = glebosaurus.token_hash(p_token) and enabled;
    if not found then
        perform glebosaurus.fail('28000', 'nieznany lub unieważniony token agenta');
    end if;
    return v_agent;
end
$$;

create function glebosaurus.seconds(p_interval interval) returns double precision
language sql immutable set search_path = '' as $$
    select round(extract(epoch from p_interval)::numeric, 3)::double precision
$$;

-- Chwila „tyle sekund temu” według zegara bazy; brak wartości zostaje brakiem (nie „teraz”).
create function glebosaurus.ago(p_now timestamptz, p_seconds double precision) returns timestamptz
language sql immutable set search_path = '' as $$
    select case when p_seconds is not null then p_now - make_interval(secs => greatest(p_seconds, 0)) end
$$;

-- Sekrety w Vault pod nazwami „glebosaurus:…”.
create function glebosaurus.secret_put(p_name text, p_value text) returns void
language plpgsql set search_path = '' as $$
declare
    v_id uuid;
begin
    if p_name !~ '^glebosaurus:' then perform glebosaurus.fail('22023', 'nazwa sekretu musi zaczynać się od glebosaurus:'); end if;
    select id into v_id from vault.secrets where name = p_name;
    if v_id is null then
        perform vault.create_secret(p_value, p_name, 'Glebosaurus');
    else
        perform vault.update_secret(v_id, p_value);
    end if;
end
$$;

create function glebosaurus.secret_get(p_name text) returns text
language sql stable set search_path = '' as $$
    select decrypted_secret from vault.decrypted_secrets where name = p_name
$$;

-- Każda zmiana ustawień podnosi wersję konfiguracji; zmiana czujników „dotyka” ustawień.
create function glebosaurus.settings_touch() returns trigger
language plpgsql set search_path = '' as $$
begin
    new.config_version := old.config_version + 1;
    new.updated_at := now();
    return new;
end
$$;
create trigger settings_touch before update on glebosaurus.settings for each row execute function glebosaurus.settings_touch();

create function glebosaurus.config_changed() returns trigger
language plpgsql set search_path = '' as $$
begin
    update glebosaurus.settings set updated_at = now();
    return null;
end
$$;
create trigger sensors_config_changed after insert or update or delete on glebosaurus.sensors
    for each statement execute function glebosaurus.config_changed();

-- ---------------------------------------------------------------------------------------------------------------------
-- Dzierżawy
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.lease_ttl(p_resource text) returns interval
language sql immutable set search_path = '' as $$
    select case when p_resource = 'notifier' then interval '5 minutes' else interval '30 minutes' end
$$;

-- Zapas po wygaśnięciu, zanim zasób dostanie ktoś inny: agent liczy ważność od wysłania prośby na własnym zegarze
-- monotonicznym, więc kończy wcześniej niż baza – zapas pokrywa opóźnienia sieci i rozjechane zegary.
create function glebosaurus.lease_grace() returns interval
language sql immutable set search_path = '' as $$
    select interval '2 minutes'
$$;

-- Przyznaje lub odnawia dzierżawę: zasób wolny, po wygaśnięciu i zapasie, albo już pytającego (odnowienie zachowuje
-- epokę; nowy właściciel dostaje epokę + 1). Zwraca opis w JSON – także odmowę z tym, kto trzyma zasób.
create function glebosaurus.lease_claim(p_agent glebosaurus.agents, p_resource text) returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_now   timestamptz := glebosaurus.clock();
    v_lease glebosaurus.leases;
    v_holder text;
begin
    if p_resource is null or p_resource !~ '^(notifier|sensor:[a-z0-9][a-z0-9_-]{0,39})$' then
        return jsonb_build_object('resource', p_resource, 'granted', false, 'error', 'invalid_resource');
    end if;
    if p_resource = 'notifier' and not p_agent.can_send_mail then
        return jsonb_build_object('resource', p_resource, 'granted', false, 'error', 'not_sender');
    end if;
    if p_resource like 'sensor:%' and not exists (
        select 1 from glebosaurus.sensors where id = substr(p_resource, 8) and enabled) then
        return jsonb_build_object('resource', p_resource, 'granted', false, 'error', 'unknown_sensor');
    end if;

    insert into glebosaurus.leases (resource) values (p_resource) on conflict do nothing;
    select * into v_lease from glebosaurus.leases where resource = p_resource for update;

    if v_lease.holder_id = p_agent.id then
        update glebosaurus.leases
           set renewed_at = v_now, expires_at = v_now + glebosaurus.lease_ttl(p_resource)
         where resource = p_resource returning * into v_lease;
    elsif v_lease.holder_id is null or v_lease.expires_at + glebosaurus.lease_grace() <= v_now then
        update glebosaurus.leases
           set holder_id = p_agent.id, epoch = epoch + 1, granted_at = v_now, renewed_at = v_now,
               expires_at = v_now + glebosaurus.lease_ttl(p_resource)
         where resource = p_resource returning * into v_lease;
    end if;

    if v_lease.holder_id = p_agent.id then
        return jsonb_build_object('resource', p_resource, 'granted', true, 'epoch', v_lease.epoch,
            'validSeconds', glebosaurus.seconds(v_lease.expires_at - v_now));
    end if;
    select name into v_holder from glebosaurus.agents where id = v_lease.holder_id;
    return jsonb_build_object('resource', p_resource, 'granted', false, 'holder', v_holder,
        'retryInSeconds', glebosaurus.seconds(greatest(v_lease.expires_at + glebosaurus.lease_grace() - v_now, interval '0')));
end
$$;

create function glebosaurus.lease_release(p_agent glebosaurus.agents, p_resource text, p_epoch bigint) returns boolean
language plpgsql set search_path = '' as $$
begin
    update glebosaurus.leases
       set holder_id = null, expires_at = null, renewed_at = glebosaurus.clock()
     where resource = p_resource and holder_id = p_agent.id and epoch = p_epoch;
    return found;
end
$$;

-- Czy agent trzyma teraz blokadę nadawcy z tą epoką (według zegara bazy). Blokuje wiersz do końca transakcji, więc
-- przejęcie blokady czeka, aż rezerwacja maila się zakończy.
create function glebosaurus.holds_notifier(p_agent glebosaurus.agents, p_epoch bigint) returns boolean
language plpgsql set search_path = '' as $$
begin
    perform 1 from glebosaurus.leases
      where resource = 'notifier' and holder_id = p_agent.id and epoch = p_epoch and expires_at > glebosaurus.clock()
      for share;
    return found;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Funkcje administracyjne – tylko właściciel przez połączenie z bazą (cloud import, przyszły panel)
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.admin_agent_upsert(p_name text, p_can_send_mail boolean, p_sender_priority int,
    p_enabled boolean default true) returns int
language plpgsql set search_path = '' as $$
declare
    v_id int;
begin
    insert into glebosaurus.agents (name, can_send_mail, sender_priority, enabled)
    values (p_name, p_can_send_mail, p_sender_priority, p_enabled)
    on conflict (name) do update
        set can_send_mail = excluded.can_send_mail, sender_priority = excluded.sender_priority, enabled = excluded.enabled
    returning id into v_id;
    return v_id;
end
$$;

-- Jednorazowy kod rejestracji (zwracany tylko raz; w bazie skrót). Rejestracja zastępuje poprzedni token agenta.
create function glebosaurus.admin_enrollment_code(p_agent_name text, p_valid_for interval default interval '24 hours')
returns text
language plpgsql set search_path = '' as $$
declare
    v_agent_id int;
    v_code text := glebosaurus.random_token('glbe_', 16);
begin
    select id into v_agent_id from glebosaurus.agents where name = p_agent_name;
    if v_agent_id is null then perform glebosaurus.fail('22023', 'nie ma agenta ' || coalesce(p_agent_name, '∅')); end if;
    if p_valid_for <= interval '0' or p_valid_for > interval '7 days' then
        perform glebosaurus.fail('22023', 'kod rejestracji może być ważny od chwili do 7 dni');
    end if;
    insert into glebosaurus.enrollment_codes (code_hash, agent_id, expires_at)
    values (glebosaurus.token_hash(v_code), v_agent_id, glebosaurus.clock() + p_valid_for);
    return v_code;
end
$$;

-- Unieważnia token od razu. Dzierżaw nie zabiera: agent bez sieci czyta do końca własnej ważności, a zasób wraca po
-- wygaśnięciu i zapasie – tak samo jak przy nagłym zniknięciu (bez dwóch właścicieli naraz).
create function glebosaurus.admin_agent_revoke(p_agent_name text) returns boolean
language plpgsql set search_path = '' as $$
begin
    update glebosaurus.agents set token_hash = null where name = p_agent_name;
    return found;
end
$$;

create function glebosaurus.admin_sensor_upsert(p_id text, p_name text, p_mac text, p_options jsonb default '{}')
returns void
language plpgsql set search_path = '' as $$
declare
    v_unknown text;
    v_mac text := upper(replace(replace(p_mac, '-', ':'), '_', ':'));
begin
    select string_agg(k, ', ') into v_unknown from jsonb_object_keys(coalesce(p_options, '{}')) k
     where k not in ('enabled', 'readIntervalMinutes', 'wateringBelowPercent', 'wateringClearAbovePercent',
                     'consecutiveReadings', 'lowBatteryPercent', 'silentAfterHours');
    if v_unknown is not null then perform glebosaurus.fail('22023', 'nieznane ustawienia czujnika: ' || v_unknown); end if;

    insert into glebosaurus.sensors (id, name, mac) values (p_id, p_name, v_mac)
    on conflict (id) do update set name = excluded.name, mac = excluded.mac;

    update glebosaurus.sensors set
        enabled = coalesce((p_options->>'enabled')::boolean, enabled),
        read_interval_minutes = case when p_options ? 'readIntervalMinutes'
            then (p_options->>'readIntervalMinutes')::numeric else read_interval_minutes end,
        watering_below_percent = coalesce((p_options->>'wateringBelowPercent')::int, watering_below_percent),
        watering_clear_above_percent = coalesce((p_options->>'wateringClearAbovePercent')::int, watering_clear_above_percent),
        consecutive_readings = coalesce((p_options->>'consecutiveReadings')::int, consecutive_readings),
        low_battery_percent = coalesce((p_options->>'lowBatteryPercent')::int, low_battery_percent),
        silent_after_hours = coalesce((p_options->>'silentAfterHours')::numeric, silent_after_hours),
        updated_at = now()
    where id = p_id;
end
$$;

-- Dane przypisania Tuya (mac, uuid, deviceId, productId, localKeyHex/localKey) do Vault. MAC musi zgadzać się z czujnikiem.
create function glebosaurus.admin_sensor_credentials(p_sensor_id text, p_credentials jsonb) returns void
language plpgsql set search_path = '' as $$
declare
    v_mac text;
begin
    select mac into v_mac from glebosaurus.sensors where id = p_sensor_id;
    if v_mac is null then perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_sensor_id, '∅')); end if;
    if jsonb_typeof(p_credentials) <> 'object'
       or length(coalesce(p_credentials->>'uuid', '')) <> 16
       or length(coalesce(p_credentials->>'deviceId', '')) not between 1 and 22
       or (p_credentials->>'localKeyHex' is null and p_credentials->>'localKey' is null) then
        perform glebosaurus.fail('22023', 'dane Tuya muszą mieć uuid (16 znaków), deviceId i localKeyHex albo localKey');
    end if;
    if upper(replace(coalesce(p_credentials->>'mac', ''), '-', ':')) <> v_mac then
        perform glebosaurus.fail('22023', 'MAC w danych Tuya nie zgadza się z czujnikiem ' || p_sensor_id);
    end if;
    perform glebosaurus.secret_put('glebosaurus:sensor:' || p_sensor_id, p_credentials::text);
    update glebosaurus.settings set updated_at = now();
end
$$;

-- Sekrety poczty: hasło aplikacji Gmail i adres sygnału healthchecks.io.
create function glebosaurus.admin_mail_secret(p_name text, p_value text) returns void
language plpgsql set search_path = '' as $$
begin
    if p_name not in ('gmail-app-password', 'healthchecks-ping-url') then
        perform glebosaurus.fail('22023', 'nieznany sekret poczty ' || coalesce(p_name, '∅'));
    end if;
    perform glebosaurus.secret_put('glebosaurus:' || p_name, p_value);
end
$$;

create function glebosaurus.admin_settings_update(p jsonb) returns void
language plpgsql set search_path = '' as $$
declare
    v_unknown text;
begin
    select string_agg(k, ', ') into v_unknown from jsonb_object_keys(coalesce(p, '{}')) k
     where k not in ('timeZone', 'readIntervalMinutes', 'scanWindowSeconds', 'reportTimes', 'reportCatchUpHours',
                     'alertReminderHours', 'logRetentionDays', 'gmailAddress', 'alertRecipients', 'reportRecipients',
                     'adminRecipients');
    if v_unknown is not null then perform glebosaurus.fail('22023', 'nieznane ustawienia: ' || v_unknown); end if;

    update glebosaurus.settings set
        time_zone = coalesce(p->>'timeZone', time_zone),
        read_interval_minutes = coalesce((p->>'readIntervalMinutes')::numeric, read_interval_minutes),
        scan_window_seconds = coalesce((p->>'scanWindowSeconds')::int, scan_window_seconds),
        report_times = case when p ? 'reportTimes'
            then array(select jsonb_array_elements_text(p->'reportTimes')) else report_times end,
        report_catch_up_hours = coalesce((p->>'reportCatchUpHours')::numeric, report_catch_up_hours),
        alert_reminder_hours = coalesce((p->>'alertReminderHours')::numeric, alert_reminder_hours),
        log_retention_days = coalesce((p->>'logRetentionDays')::int, log_retention_days),
        gmail_address = case when p ? 'gmailAddress' then p->>'gmailAddress' else gmail_address end,
        alert_recipients = case when p ? 'alertRecipients'
            then array(select jsonb_array_elements_text(p->'alertRecipients')) else alert_recipients end,
        report_recipients = case when p ? 'reportRecipients'
            then array(select jsonb_array_elements_text(p->'reportRecipients')) else report_recipients end,
        admin_recipients = case when p ? 'adminRecipients'
            then array(select jsonb_array_elements_text(p->'adminRecipients')) else admin_recipients end;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API agentów (public, Data API: POST /rest/v1/rpc/<nazwa>, klucz publikowalny w nagłówku apikey)
-- ---------------------------------------------------------------------------------------------------------------------

-- Jednorazowy kod → stały token agenta (zwracany tylko tutaj; w bazie skrót).
create function public.enroll(p_code text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now   timestamptz := glebosaurus.clock();
    v_code  glebosaurus.enrollment_codes;
    v_agent glebosaurus.agents;
    v_token text := glebosaurus.random_token('glb_', 32);
begin
    select * into v_code from glebosaurus.enrollment_codes
     where code_hash = glebosaurus.token_hash(coalesce(p_code, '')) for update;
    if not found or v_code.used_at is not null or v_code.expires_at <= v_now then
        perform glebosaurus.fail('28000', 'kod rejestracji jest nieznany, zużyty albo wygasł');
    end if;
    update glebosaurus.enrollment_codes set used_at = v_now where code_hash = v_code.code_hash;
    update glebosaurus.agents set token_hash = glebosaurus.token_hash(v_token), enrolled_at = v_now
     where id = v_code.agent_id and enabled returning * into v_agent;
    if not found then perform glebosaurus.fail('28000', 'agent jest wyłączony'); end if;
    return jsonb_build_object('protocol', 1, 'agent', v_agent.name, 'token', v_token,
        'canSendMail', v_agent.can_send_mail, 'senderPriority', v_agent.sender_priority);
end
$$;

-- Konfiguracja dla agenta: ustawienia, czujniki z danymi Tuya (każdy agent może przejąć każdy czujnik). Odbiorców i
-- adres nadawcy dostają tylko kandydaci na nadawcę; hasła Gmaila – nikt (tylko mail_credentials z blokadą).
create function public.get_config(p_token text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_settings glebosaurus.settings;
    v_mail jsonb := null;
begin
    select * into v_settings from glebosaurus.settings;
    if v_agent.can_send_mail then
        v_mail := jsonb_build_object(
            'gmailAddress', v_settings.gmail_address,
            'alertRecipients', to_jsonb(v_settings.alert_recipients),
            'reportRecipients', to_jsonb(v_settings.report_recipients),
            'adminRecipients', to_jsonb(v_settings.admin_recipients));
    end if;
    return jsonb_build_object(
        'protocol', 1,
        'configVersion', v_settings.config_version,
        'agent', jsonb_build_object('name', v_agent.name, 'canSendMail', v_agent.can_send_mail,
                                    'senderPriority', v_agent.sender_priority),
        'settings', jsonb_build_object(
            'timeZone', v_settings.time_zone,
            'readIntervalMinutes', v_settings.read_interval_minutes,
            'scanWindowSeconds', v_settings.scan_window_seconds,
            'reportTimes', to_jsonb(v_settings.report_times),
            'reportCatchUpHours', v_settings.report_catch_up_hours,
            'alertReminderHours', v_settings.alert_reminder_hours,
            'logRetentionDays', v_settings.log_retention_days),
        'mail', v_mail,
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', s.read_interval_minutes,
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'consecutiveReadings', s.consecutive_readings,
                'lowBatteryPercent', s.low_battery_percent,
                'silentAfterHours', s.silent_after_hours,
                'tuya', glebosaurus.secret_get('glebosaurus:sensor:' || s.id)::jsonb) order by s.id)
            from glebosaurus.sensors s), '[]'::jsonb));
end
$$;

-- Główne wywołanie agenta co ok. 60 s (ARCHITEKTURA.md, zasada 3). Ładunek:
--   protocol   1
--   status     dowolny obiekt (wersja, stan BLE; telefon: bateria, temperatura…) – sygnał życia
--   sightings  [{sensor, rssi, heardSecondsAgo, lastAttemptSecondsAgo, lastSuccessSecondsAgo, failures}] – czasy
--              względne, bo zegar agenta może się rozjechać z bazą
--   readings   [{sensor, at, moisture, tempTenths, battery, batteryState}] – najwyżej 1000; idempotentne
--   releases   [{resource, epoch}] – najpierw oddania…
--   claims     ["sensor:<id>", "notifier"] – …potem prośby i odnowienia (w stałej kolejności, bez zakleszczeń)
-- Odpowiedź: czas bazy, wynik odczytów (odrzucone z powodem), dzierżawy, stan czujników z najlepszym słyszeniem innego
-- agenta (do oddawania czujników), blokada nadawcy i lista agentów (kandydaci, cisza agentów), wersja konfiguracji.
create function public.agent_sync(p_token text, p_payload jsonb) returns jsonb
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

    -- Odczyty: poprawne trafiają do bazy (powtórzone bez zmian), odrzucone wracają z powodem – agent ich nie ponawia.
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

    -- Słyszenia: po jednym wierszu na czujnik (przy powtórzeniu w paczce wygrywa najświeższe).
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

    return jsonb_build_object(
        'protocol', 1,
        'serverTime', v_now,
        'configVersion', (select config_version from glebosaurus.settings),
        'agent', jsonb_build_object('name', v_agent.name, 'canSendMail', v_agent.can_send_mail,
                                    'senderPriority', v_agent.sender_priority),
        'readings', jsonb_build_object('received', v_received, 'inserted', v_inserted, 'rejected', v_rejected),
        'released', v_released,
        'leases', v_leases,
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

create function public.lease_claim(p_token text, p_resource text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    return glebosaurus.lease_claim(glebosaurus.agent_auth(p_token), p_resource);
end
$$;

create function public.lease_release(p_token text, p_resource text, p_epoch bigint) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    return glebosaurus.lease_release(glebosaurus.agent_auth(p_token), p_resource, p_epoch);
end
$$;

-- Rezerwacja maila: tylko obecny nadawca (blokada z tą epoką, ważna według zegara bazy) i tylko raz na klucz.
-- Wynik: claimed + reason: ok | not_holder | already_claimed (sentAt, gdy już wysłany).
create function public.mail_claim(p_token text, p_key text, p_epoch bigint) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_now   timestamptz := glebosaurus.clock();
    v_row   glebosaurus.mail_outbox;
begin
    if p_key is null or p_key !~ '^(report|alert|log|agent-silent|report-request)\|.+$' or length(p_key) > 200 then
        perform glebosaurus.fail('22023', 'nieprawidłowy klucz maila');
    end if;
    if not glebosaurus.holds_notifier(v_agent, p_epoch) then
        return jsonb_build_object('claimed', false, 'reason', 'not_holder');
    end if;
    insert into glebosaurus.mail_outbox (key, agent_id, epoch, claimed_at) values (p_key, v_agent.id, p_epoch, v_now)
    on conflict do nothing;
    if found then
        return jsonb_build_object('claimed', true, 'reason', 'ok');
    end if;
    select * into v_row from glebosaurus.mail_outbox where key = p_key;
    return jsonb_build_object('claimed', false, 'reason', 'already_claimed', 'sentAt', v_row.sent_at);
end
$$;

create function public.mail_sent(p_token text, p_key text, p_message_id text default null) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    update glebosaurus.mail_outbox set sent_at = glebosaurus.clock(), message_id = left(p_message_id, 250)
     where key = p_key and agent_id = v_agent.id and sent_at is null;
    return found;
end
$$;

-- Oddanie rezerwacji, gdy SMTP na pewno nie przyjął maila (np. odmowa przed wysłaniem treści) – wtedy można go wysłać
-- jeszcze raz. Przy niepewnym wyniku (przerwa po wysłaniu treści) rezerwacja zostaje: lepiej zgubić mail niż go zdublować.
create function public.mail_abandon(p_token text, p_key text) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    delete from glebosaurus.mail_outbox where key = p_key and agent_id = v_agent.id and sent_at is null;
    return found;
end
$$;

-- Hasło aplikacji Gmail i adres sygnału healthchecks.io – tylko dla obecnego nadawcy; agent trzyma je wyłącznie w pamięci.
create function public.mail_credentials(p_token text, p_epoch bigint) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    if not glebosaurus.holds_notifier(v_agent, p_epoch) then
        perform glebosaurus.fail('42501', 'hasło poczty dostaje tylko obecny nadawca');
    end if;
    return jsonb_build_object(
        'gmailAddress', (select gmail_address from glebosaurus.settings),
        'appPassword', glebosaurus.secret_get('glebosaurus:gmail-app-password'),
        'healthchecksPingUrl', glebosaurus.secret_get('glebosaurus:healthchecks-ping-url'));
end
$$;

-- Odczyty czujników z przedziału (raporty, alerty, wykresy) – najwyżej 50 000 wierszy, od najstarszego.
create function public.readings_range(p_token text, p_sensors text[], p_from timestamptz, p_to timestamptz) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.agent_auth(p_token);
    if p_from is null or p_to is null or p_to < p_from or p_to - p_from > interval '400 days' then
        perform glebosaurus.fail('22023', 'przedział odczytów musi mieć od 0 do 400 dni');
    end if;
    return coalesce((
        select jsonb_agg(jsonb_build_object('sensor', sensor_id, 'at', at, 'moisture', moisture,
                                            'tempTenths', temp_tenths, 'battery', battery, 'batteryState', battery_state)
                         order by sensor_id, at)
          from (select * from glebosaurus.readings
                 where sensor_id = any(p_sensors) and at between p_from and p_to
                 order by sensor_id, at limit 50000) r), '[]'::jsonb);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia: agenci (anon) wołają tylko funkcje API; nic w schemacie glebosaurus.
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.enroll(text) from public, anon, authenticated, service_role;
revoke all on function public.get_config(text) from public, anon, authenticated, service_role;
revoke all on function public.agent_sync(text, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.lease_claim(text, text) from public, anon, authenticated, service_role;
revoke all on function public.lease_release(text, text, bigint) from public, anon, authenticated, service_role;
revoke all on function public.mail_claim(text, text, bigint) from public, anon, authenticated, service_role;
revoke all on function public.mail_sent(text, text, text) from public, anon, authenticated, service_role;
revoke all on function public.mail_abandon(text, text) from public, anon, authenticated, service_role;
revoke all on function public.mail_credentials(text, bigint) from public, anon, authenticated, service_role;
revoke all on function public.readings_range(text, text[], timestamptz, timestamptz) from public, anon, authenticated, service_role;

grant execute on function public.enroll(text) to anon;
grant execute on function public.get_config(text) to anon;
grant execute on function public.agent_sync(text, jsonb) to anon;
grant execute on function public.lease_claim(text, text) to anon;
grant execute on function public.lease_release(text, text, bigint) to anon;
grant execute on function public.mail_claim(text, text, bigint) to anon;
grant execute on function public.mail_sent(text, text, text) to anon;
grant execute on function public.mail_abandon(text, text) to anon;
grant execute on function public.mail_credentials(text, bigint) to anon;
grant execute on function public.readings_range(text, text[], timestamptz, timestamptz) to anon;

-- Znacznik funkcji API (testy na projekcie próbnym usuwają po nim funkcje przed ponownym zastosowaniem migracji).
comment on function public.enroll(text) is 'glebosaurus-api';
comment on function public.get_config(text) is 'glebosaurus-api';
comment on function public.agent_sync(text, jsonb) is 'glebosaurus-api';
comment on function public.lease_claim(text, text) is 'glebosaurus-api';
comment on function public.lease_release(text, text, bigint) is 'glebosaurus-api';
comment on function public.mail_claim(text, text, bigint) is 'glebosaurus-api';
comment on function public.mail_sent(text, text, text) is 'glebosaurus-api';
comment on function public.mail_abandon(text, text) is 'glebosaurus-api';
comment on function public.mail_credentials(text, bigint) is 'glebosaurus-api';
comment on function public.readings_range(text, text[], timestamptz, timestamptz) is 'glebosaurus-api';
