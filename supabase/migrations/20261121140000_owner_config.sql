-- Glebosaurus, faza 3 (etap 2, 3c) – edytor konfiguracji na stronie właściciela (docs/ARCHITEKTURA.md, zasady 4, 10 i 13):
-- ustawienia, poczta i odbiorcy, czujniki (dodanie z plikiem klucza z „pair”, progi, archiwum zamiast usuwania), agenci
-- (nowy z kodem rejestracji, rola, wyłączenie, unieważnienie), sekrety tylko do ustawienia, próbny mail przez nadawcę.
--
-- Reguły walidacji są te same co w CloudSetupLoader (C#) – pilnuje tego test kontraktowy (ConfigContractTests); gdzie
-- dotąd SQL był luźniejszy (okno skanu do 600 s, consecutiveReadings do 20, clearAbove ≥ below), funkcje strony
-- stosują reguły pliku. Każdy zapis konfiguracji niesie config_version odczytaną przez stronę: inna wersja w bazie →
-- 40001 „conflict” (druga karta albo agent… nie nadpisze cudzej zmiany po cichu).
--
-- Zgodność ze starszymi agentami: tylko nowe kolumny, rodzaje poleceń i funkcje; get_config pomija czujniki z archiwum
-- (dla agenta to jak usunięty czujnik), a nieznane polecenie (mail_test) starszy agent zostawia, aż wygaśnie.

-- ---------------------------------------------------------------------------------------------------------------------
-- Schemat
-- ---------------------------------------------------------------------------------------------------------------------

-- Archiwum: czujnik znika z pulpitu, maili i agentów, a historia odczytów zostaje (przywrócenie jednym kliknięciem).
alter table glebosaurus.sensors add column archived_at timestamptz;
alter table glebosaurus.sensors add constraint sensors_archived_disabled check (archived_at is null or not enabled);

-- Próbny mail (zastępuje mail-test z pliku): polecenie do obecnego nadawcy i klucz kolejki „mail-test|<id polecenia>”.
alter table glebosaurus.commands drop constraint commands_kind_check;
alter table glebosaurus.commands add constraint commands_kind_check
    check (kind in ('read_now', 'report_now', 'release', 'pair', 'send_log', 'mail_test'));
alter table glebosaurus.mail_outbox drop constraint mail_outbox_key_check;
alter table glebosaurus.mail_outbox add constraint mail_outbox_key_check
    check (key ~ '^(report|alert|log|agent-silent|report-request|mail-test)\|.+$' and length(key) <= 200);

-- ---------------------------------------------------------------------------------------------------------------------
-- Polecenia: mail_test jak report_now – do obecnego nadawcy
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261116220000_alerts_commands_logs.sql; zmiana: rodzaj mail_test.
create or replace function glebosaurus.command_create(p_kind text, p_sensor_id text, p_target_agent_id int, p_params jsonb,
    p_created_by text) returns jsonb
language plpgsql set search_path = '' as $$
declare
    v_now      timestamptz := glebosaurus.clock();
    v_existing glebosaurus.commands;
    v_id       bigint;
begin
    if p_kind is null or p_kind not in ('read_now', 'report_now', 'release', 'pair', 'send_log', 'mail_test') then
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

-- Jak w 20261116220000_alerts_commands_logs.sql; zmiana: mail_test trafia do obecnego nadawcy (jak report_now).
create or replace function glebosaurus.commands_for(p_agent glebosaurus.agents, p_now timestamptz) returns jsonb
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
                    or (c.target_agent_id is null and c.kind in ('report_now', 'mail_test') and exists (
                            select 1 from glebosaurus.leases l
                             where l.resource = 'notifier' and l.holder_id = p_agent.id and l.expires_at > p_now)))
             order by c.id limit 50) c;

    update glebosaurus.commands set taken_by = p_agent.id, taken_at = p_now
     where id in (select (e->>'id')::bigint from jsonb_array_elements(v_commands) e)
       and taken_by is distinct from p_agent.id;
    return v_commands;
end
$$;

-- Jak w 20261116200000_core.sql; zmiana: klucz „mail-test|…”.
create or replace function public.mail_claim(p_token text, p_key text, p_epoch bigint) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_now   timestamptz := glebosaurus.clock();
    v_row   glebosaurus.mail_outbox;
begin
    if p_key is null or p_key !~ '^(report|alert|log|agent-silent|report-request|mail-test)\|.+$' or length(p_key) > 200 then
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

-- ---------------------------------------------------------------------------------------------------------------------
-- Agenci nie widzą czujników z archiwum
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261116200000_core.sql; zmiana: bez czujników z archiwum.
create or replace function public.get_config(p_token text) returns jsonb
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
            from glebosaurus.sensors s where s.archived_at is null), '[]'::jsonb));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Walidacja (te same reguły co CloudSetupLoader.Validate – test kontraktowy)
-- ---------------------------------------------------------------------------------------------------------------------

create function glebosaurus.known_time_zone(p_zone text) returns boolean
language sql stable set search_path = '' as $$
    select p_zone is not null and p_zone ~ '^[A-Za-z]+(/[A-Za-z0-9_+-]+)+$|^UTC$'
       and exists (select 1 from pg_catalog.pg_timezone_names where name = p_zone)
$$;

-- Liczba z JSON (bez napisów udających liczby); null, gdy klucza nie ma albo jest null.
create function glebosaurus.json_num(p jsonb, p_key text, p_label text) returns numeric
language plpgsql immutable set search_path = '' as $$
begin
    if not (p ? p_key) or jsonb_typeof(p->p_key) = 'null' then return null; end if;
    if jsonb_typeof(p->p_key) <> 'number' then perform glebosaurus.fail('22023', p_label || ': podaj liczbę'); end if;
    return (p->>p_key)::numeric;
end
$$;

create function glebosaurus.json_int(p jsonb, p_key text, p_label text) returns int
language plpgsql immutable set search_path = '' as $$
declare
    v numeric := glebosaurus.json_num(p, p_key, p_label);
begin
    if v is not null and v <> trunc(v) then perform glebosaurus.fail('22023', p_label || ': podaj liczbę całkowitą'); end if;
    if v is not null and abs(v) > 1000000 then perform glebosaurus.fail('22023', p_label || ': liczba poza zakresem'); end if;
    return v::int;
end
$$;

create function glebosaurus.json_text(p jsonb, p_key text, p_label text) returns text
language plpgsql immutable set search_path = '' as $$
begin
    if not (p ? p_key) or jsonb_typeof(p->p_key) = 'null' then return null; end if;
    if jsonb_typeof(p->p_key) <> 'string' then perform glebosaurus.fail('22023', p_label || ': podaj tekst'); end if;
    return p->>p_key;
end
$$;

create function glebosaurus.json_bool(p jsonb, p_key text, p_label text) returns boolean
language plpgsql immutable set search_path = '' as $$
begin
    if not (p ? p_key) or jsonb_typeof(p->p_key) = 'null' then return null; end if;
    if jsonb_typeof(p->p_key) <> 'boolean' then perform glebosaurus.fail('22023', p_label || ': podaj tak albo nie'); end if;
    return (p->>p_key)::boolean;
end
$$;

-- Sam adres (bez „Imię <adres>”), z domeną zawierającą kropkę – jak IsEmailAddress w CloudSetupLoader.
create function glebosaurus.email_ok(p text) returns boolean
language sql immutable set search_path = '' as $$
    select p is not null and length(p) <= 254 and p ~ '^[^@[:space:]<>(),;:"\[\]\\]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$'
$$;

-- Lista adresów: obcięte spacje, każdy sprawdzony, powtórzenia (bez względu na wielkość liter) usunięte, kolejność zostaje.
create function glebosaurus.normalize_addresses(p jsonb, p_label text) returns text[]
language plpgsql immutable set search_path = '' as $$
declare
    v_result text[] := '{}';
    v_item   jsonb;
    v_text   text;
begin
    if p is null or jsonb_typeof(p) = 'null' then return v_result; end if;
    if jsonb_typeof(p) <> 'array' then perform glebosaurus.fail('22023', p_label || ': podaj listę adresów'); end if;
    if jsonb_array_length(p) > 50 then perform glebosaurus.fail('22023', p_label || ': najwyżej 50 adresów'); end if;
    for v_item in select value from jsonb_array_elements(p) loop
        if jsonb_typeof(v_item) <> 'string' then perform glebosaurus.fail('22023', p_label || ': podaj adresy jako tekst'); end if;
        v_text := trim(v_item #>> '{}');
        if not glebosaurus.email_ok(v_text) then
            perform glebosaurus.fail('22023', p_label || ': „' || left(v_item #>> '{}', 80) ||
                                              '” nie wygląda jak adres e-mail (podaj sam adres, np. ktos@example.com)');
        end if;
        if not exists (select 1 from unnest(v_result) r where lower(r) = lower(v_text)) then
            v_result := v_result || v_text;
        end if;
    end loop;
    return v_result;
end
$$;

-- Sprawdza i ujednolica dane przypisania Tuya z pliku klucza „pair” (TuyaCredentials.Validate + znany productId + MAC).
-- Zwraca obiekt do Vault z samymi znanymi polami; MAC w postaci AA:BB:CC:DD:EE:FF.
create function glebosaurus.normalize_credentials(p jsonb) returns jsonb
language plpgsql immutable set search_path = '' as $$
declare
    v_mac    text;
    v_uuid   text;
    v_device text;
    v_hex    text;
    v_key    text;
    v_product text;
begin
    if p is null or jsonb_typeof(p) <> 'object' then
        perform glebosaurus.fail('22023', 'plik klucza: to nie jest obiekt JSON z polecenia pair');
    end if;
    v_mac := upper(coalesce(glebosaurus.json_text(p, 'mac', 'plik klucza: mac'), ''));
    if v_mac !~ '^[0-9A-F]{2}([:-]?[0-9A-F]{2}){5}$' then
        perform glebosaurus.fail('22023', 'plik klucza: brak adresu MAC albo zły zapis – chmura rozpoznaje czujnik po MAC (adres podaje glebosaurus scan)');
    end if;
    v_mac := regexp_replace(replace(v_mac, '-', ''), '[:]', '', 'g');
    v_mac := substr(v_mac, 1, 2) || ':' || substr(v_mac, 3, 2) || ':' || substr(v_mac, 5, 2) || ':' ||
             substr(v_mac, 7, 2) || ':' || substr(v_mac, 9, 2) || ':' || substr(v_mac, 11, 2);
    v_uuid := coalesce(glebosaurus.json_text(p, 'uuid', 'plik klucza: uuid'), '');
    v_device := coalesce(glebosaurus.json_text(p, 'deviceId', 'plik klucza: deviceId'), '');
    if length(v_uuid) <> 16 or v_uuid !~ '^[ -~]+$' or length(v_device) not between 1 and 22 or v_device !~ '^[!-~]+$' then
        perform glebosaurus.fail('22023', 'plik klucza: UUID musi mieć 16 znaków ASCII, deviceId od 1 do 22 znaków ASCII');
    end if;
    v_hex := nullif(glebosaurus.json_text(p, 'localKeyHex', 'plik klucza: localKeyHex'), '');
    v_key := nullif(glebosaurus.json_text(p, 'localKey', 'plik klucza: localKey'), '');
    if v_hex is not null and v_key is not null then
        perform glebosaurus.fail('22023', 'plik klucza: podaj tylko localKey albo localKeyHex');
    end if;
    if v_hex is not null and v_hex !~ '^[0-9A-Fa-f]{12}$' then
        perform glebosaurus.fail('22023', 'plik klucza: localKeyHex musi zawierać dokładnie 6 bajtów (12 znaków HEX)');
    end if;
    if v_hex is null and (v_key is null or length(v_key) < 6 or v_key !~ '^[\x01-\x7f]+$') then
        perform glebosaurus.fail('22023', 'plik klucza: localKey musi zawierać co najmniej 6 znaków ASCII');
    end if;
    v_product := glebosaurus.json_text(p, 'productId', 'plik klucza: productId');
    if v_product is null or v_product not in ('gvygg3m8', 'jabotj1z', 'ojzlzzsw') then
        perform glebosaurus.fail('22023', 'plik klucza: nieznany productId „' || coalesce(v_product, '') || '”');
    end if;
    return jsonb_strip_nulls(jsonb_build_object('mac', v_mac, 'uuid', v_uuid, 'deviceId', v_device, 'productId', v_product,
        'localKeyHex', lower(v_hex), 'localKey', v_key));
end
$$;

-- Zapis konfiguracji tylko na wersji, którą strona widziała. Blokuje wiersz ustawień do końca transakcji.
create function glebosaurus.require_config_version(p_version bigint) returns void
language plpgsql set search_path = '' as $$
declare
    v_current bigint;
begin
    select config_version into v_current from glebosaurus.settings for update;
    if p_version is distinct from v_current then
        perform glebosaurus.fail('40001', 'conflict: konfigurację zmieniono w międzyczasie (teraz wersja ' || v_current ||
                                          ') – odśwież stronę i wprowadź zmianę jeszcze raz');
    end if;
end
$$;

-- Ustawienia czujnika z formularza strony (wszystkie poza id, nazwą i MAC). Zwraca wartości do zapisu.
create function glebosaurus.sensor_options(p jsonb, p_label text) returns jsonb
language plpgsql immutable set search_path = '' as $$
declare
    v_unknown  text;
    v_interval numeric;
    v_below    int;
    v_clear    int;
    v_count    int;
    v_battery  int;
    v_silent   numeric;
begin
    if p is null or jsonb_typeof(p) <> 'object' then perform glebosaurus.fail('22023', p_label || ': brak ustawień'); end if;
    select string_agg(k, ', ') into v_unknown from jsonb_object_keys(p) k
     where k not in ('enabled', 'readIntervalMinutes', 'wateringBelowPercent', 'wateringClearAbovePercent',
                     'consecutiveReadings', 'lowBatteryPercent', 'silentAfterHours');
    if v_unknown is not null then perform glebosaurus.fail('22023', p_label || ': nieznane ustawienia ' || v_unknown); end if;
    v_interval := glebosaurus.json_num(p, 'readIntervalMinutes', p_label || ': readIntervalMinutes');
    if v_interval is not null and (v_interval < 0.5 or v_interval > 10080) then
        perform glebosaurus.fail('22023', p_label || ': readIntervalMinutes: minimum 0,5');
    end if;
    v_below := coalesce(glebosaurus.json_int(p, 'wateringBelowPercent', p_label || ': wateringBelowPercent'), 25);
    v_clear := coalesce(glebosaurus.json_int(p, 'wateringClearAbovePercent', p_label || ': wateringClearAbovePercent'), 35);
    if v_below not between 0 and 100 or v_clear not between 0 and 100 then
        perform glebosaurus.fail('22023', p_label || ': progi wilgotności muszą mieścić się w 0–100');
    end if;
    if v_clear <= v_below then
        perform glebosaurus.fail('22023', p_label || ': wateringClearAbovePercent musi być większy niż wateringBelowPercent');
    end if;
    v_count := coalesce(glebosaurus.json_int(p, 'consecutiveReadings', p_label || ': consecutiveReadings'), 2);
    if v_count not between 1 and 10 then perform glebosaurus.fail('22023', p_label || ': consecutiveReadings: od 1 do 10'); end if;
    v_battery := coalesce(glebosaurus.json_int(p, 'lowBatteryPercent', p_label || ': lowBatteryPercent'), 18);
    if v_battery not between 0 and 100 then perform glebosaurus.fail('22023', p_label || ': lowBatteryPercent: od 0 do 100'); end if;
    v_silent := coalesce(glebosaurus.json_num(p, 'silentAfterHours', p_label || ': silentAfterHours'), 6);
    if v_silent <= 0 or v_silent > 8760 then perform glebosaurus.fail('22023', p_label || ': silentAfterHours: musi być dodatnie'); end if;
    return jsonb_build_object('enabled', coalesce(glebosaurus.json_bool(p, 'enabled', p_label || ': enabled'), true),
        'readIntervalMinutes', v_interval, 'wateringBelowPercent', v_below, 'wateringClearAbovePercent', v_clear,
        'consecutiveReadings', v_count, 'lowBatteryPercent', v_battery, 'silentAfterHours', v_silent);
end
$$;

create function glebosaurus.sensor_name(p_name text, p_id text) returns text
language plpgsql immutable set search_path = '' as $$
declare
    v text := trim(coalesce(p_name, ''));
begin
    if v = '' then v := p_id; end if;
    if length(v) > 80 then perform glebosaurus.fail('22023', 'czujnik ' || p_id || ': nazwa dłuższa niż 80 znaków'); end if;
    return v;
end
$$;

-- MAC i UUID nie mogą się powtarzać między czujnikami (także z archiwum – fizycznie to ten sam czujnik).
create function glebosaurus.require_unique_device(p_sensor_id text, p_credentials jsonb) returns void
language plpgsql set search_path = '' as $$
declare
    v_other text;
begin
    select id into v_other from glebosaurus.sensors where mac = p_credentials->>'mac' and id <> p_sensor_id;
    if v_other is not null then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor_id || ': ten sam adres MAC co czujnik ' || v_other);
    end if;
    select s.id into v_other from glebosaurus.sensors s
     where s.id <> p_sensor_id
       and glebosaurus.secret_get('glebosaurus:sensor:' || s.id)::jsonb->>'uuid' = p_credentials->>'uuid'
     limit 1;
    if v_other is not null then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor_id || ': ten sam UUID Tuya co czujnik ' || v_other);
    end if;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: odczyt konfiguracji
-- ---------------------------------------------------------------------------------------------------------------------

-- Cała konfiguracja bez sekretów: sekrety tylko jako „ustawione” z datą zmiany.
create function public.owner_config() returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_settings glebosaurus.settings;
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    select * into v_settings from glebosaurus.settings;
    return jsonb_build_object(
        'serverTime', v_now,
        'configVersion', v_settings.config_version,
        'settings', jsonb_build_object(
            'timeZone', v_settings.time_zone,
            'readIntervalMinutes', v_settings.read_interval_minutes,
            'scanWindowSeconds', v_settings.scan_window_seconds,
            'reportTimes', to_jsonb(v_settings.report_times),
            'reportCatchUpHours', v_settings.report_catch_up_hours,
            'alertReminderHours', v_settings.alert_reminder_hours,
            'logRetentionDays', v_settings.log_retention_days),
        'mail', jsonb_build_object(
            'gmailAddress', v_settings.gmail_address,
            'alertRecipients', to_jsonb(v_settings.alert_recipients),
            'reportRecipients', to_jsonb(v_settings.report_recipients),
            'adminRecipients', to_jsonb(v_settings.admin_recipients),
            'appPasswordSetAt', (select updated_at from vault.secrets where name = 'glebosaurus:gmail-app-password')),
        'healthchecksSetAt', (select updated_at from vault.secrets where name = 'glebosaurus:healthchecks-ping-url'),
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled, 'archivedAt', s.archived_at,
                'readIntervalMinutes', s.read_interval_minutes,
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'consecutiveReadings', s.consecutive_readings,
                'lowBatteryPercent', s.low_battery_percent,
                'silentAfterHours', s.silent_after_hours,
                'createdAt', s.created_at,
                'keySetAt', (select v.updated_at from vault.secrets v where v.name = 'glebosaurus:sensor:' || s.id),
                'hasReadings', exists (select 1 from glebosaurus.readings r where r.sensor_id = s.id))
                order by s.archived_at nulls first, s.name, s.id)
              from glebosaurus.sensors s), '[]'::jsonb),
        'agents', coalesce((
            select jsonb_agg(jsonb_build_object(
                'name', a.name, 'canSendMail', a.can_send_mail, 'senderPriority', a.sender_priority,
                'enabled', a.enabled, 'enrolled', a.token_hash is not null, 'enrolledAt', a.enrolled_at,
                'lastSeenAt', a.last_seen_at, 'createdAt', a.created_at,
                'codeValidUntil', (select max(c.expires_at) from glebosaurus.enrollment_codes c
                                    where c.agent_id = a.id and c.used_at is null and c.expires_at > v_now),
                'status', glebosaurus.agent_status_view(a.last_status)) order by a.name)
              from glebosaurus.agents a), '[]'::jsonb));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: zapis konfiguracji (z config_version)
-- ---------------------------------------------------------------------------------------------------------------------

-- Ustawienia wspólne i poczta (bez hasła). Klucz nieobecny – bez zmian; zwraca nową wersję konfiguracji.
create function public.owner_settings_update(p_version bigint, p jsonb) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_unknown  text;
    v_settings glebosaurus.settings;
    v_zone     text;
    v_num      numeric;
    v_int      int;
    v_times    text[];
    v_gmail    text;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if p is null or jsonb_typeof(p) <> 'object' then perform glebosaurus.fail('22023', 'brak ustawień'); end if;
    select string_agg(k, ', ') into v_unknown from jsonb_object_keys(p) k
     where k not in ('timeZone', 'readIntervalMinutes', 'scanWindowSeconds', 'reportTimes', 'reportCatchUpHours',
                     'alertReminderHours', 'logRetentionDays', 'gmailAddress', 'alertRecipients', 'reportRecipients',
                     'adminRecipients');
    if v_unknown is not null then perform glebosaurus.fail('22023', 'nieznane ustawienia: ' || v_unknown); end if;
    select * into v_settings from glebosaurus.settings;

    if p ? 'timeZone' then
        v_zone := trim(coalesce(glebosaurus.json_text(p, 'timeZone', 'timeZone'), ''));
        if not glebosaurus.known_time_zone(v_zone) then
            perform glebosaurus.fail('22023', 'timeZone: strefa „' || left(v_zone, 60) || '” jest nieznana – podaj nazwę IANA, np. Europe/Warsaw');
        end if;
        v_settings.time_zone := v_zone;
    end if;
    if p ? 'readIntervalMinutes' then
        v_num := glebosaurus.json_num(p, 'readIntervalMinutes', 'readIntervalMinutes');
        if v_num is null or v_num < 0.5 or v_num > 10080 then perform glebosaurus.fail('22023', 'readIntervalMinutes: minimum 0,5'); end if;
        v_settings.read_interval_minutes := v_num;
    end if;
    if p ? 'scanWindowSeconds' then
        v_int := glebosaurus.json_int(p, 'scanWindowSeconds', 'scanWindowSeconds');
        if v_int is null or v_int not between 5 and 120 then perform glebosaurus.fail('22023', 'scanWindowSeconds: od 5 do 120'); end if;
        v_settings.scan_window_seconds := v_int;
    end if;
    if p ? 'reportTimes' then
        if jsonb_typeof(p->'reportTimes') <> 'array' or jsonb_array_length(p->'reportTimes') > 24
           or exists (select 1 from jsonb_array_elements(p->'reportTimes') t where jsonb_typeof(t) <> 'string') then
            perform glebosaurus.fail('22023', 'reportTimes: podaj listę godzin HH:mm (najwyżej 24)');
        end if;
        select array_agg(distinct trim(t) order by trim(t)) into v_times from jsonb_array_elements_text(p->'reportTimes') t;
        v_times := coalesce(v_times, '{}');
        if not glebosaurus.valid_times(v_times) then
            perform glebosaurus.fail('22023', 'reportTimes: „' || (select left(t, 20) from unnest(v_times) t
                where t !~ '^([01][0-9]|2[0-3]):[0-5][0-9]$' limit 1) || '” nie jest godziną HH:mm');
        end if;
        v_settings.report_times := v_times;
    end if;
    if p ? 'reportCatchUpHours' then
        v_num := glebosaurus.json_num(p, 'reportCatchUpHours', 'reportCatchUpHours');
        if v_num is null or v_num < 0 or v_num > 24 then perform glebosaurus.fail('22023', 'reportCatchUpHours: od 0 do 24'); end if;
        v_settings.report_catch_up_hours := v_num;
    end if;
    if p ? 'alertReminderHours' then
        v_num := glebosaurus.json_num(p, 'alertReminderHours', 'alertReminderHours');
        if v_num is null or v_num <= 0 or v_num > 8760 then perform glebosaurus.fail('22023', 'alertReminderHours: musi być dodatnie'); end if;
        v_settings.alert_reminder_hours := v_num;
    end if;
    if p ? 'logRetentionDays' then
        v_int := glebosaurus.json_int(p, 'logRetentionDays', 'logRetentionDays');
        if v_int is null or v_int not between 1 and 30 then
            perform glebosaurus.fail('22023', 'logRetentionDays: od 1 do 30 (dzienniki agentów leżą w bazie w chmurze)');
        end if;
        v_settings.log_retention_days := v_int;
    end if;
    if p ? 'gmailAddress' then
        v_gmail := nullif(trim(coalesce(glebosaurus.json_text(p, 'gmailAddress', 'gmailAddress'), '')), '');
        if v_gmail is not null and not glebosaurus.email_ok(v_gmail) then
            perform glebosaurus.fail('22023', 'gmailAddress: podaj adres konta Gmail');
        end if;
        v_settings.gmail_address := v_gmail;
    end if;
    if p ? 'alertRecipients' then v_settings.alert_recipients := glebosaurus.normalize_addresses(p->'alertRecipients', 'alertRecipients'); end if;
    if p ? 'reportRecipients' then v_settings.report_recipients := glebosaurus.normalize_addresses(p->'reportRecipients', 'reportRecipients'); end if;
    if p ? 'adminRecipients' then v_settings.admin_recipients := glebosaurus.normalize_addresses(p->'adminRecipients', 'adminRecipients'); end if;
    -- Jak sekcja „mail” pliku: sprawdzane, gdy zapis dotyczy poczty (zmiana samych pór raportów nie wymaga poprawy poczty).
    if (p ? 'gmailAddress' or p ? 'alertRecipients')
       and v_settings.gmail_address is not null and cardinality(v_settings.alert_recipients) = 0 then
        perform glebosaurus.fail('22023', 'alertRecipients: podaj co najmniej jeden adres');
    end if;

    update glebosaurus.settings set
        time_zone = v_settings.time_zone, read_interval_minutes = v_settings.read_interval_minutes,
        scan_window_seconds = v_settings.scan_window_seconds, report_times = v_settings.report_times,
        report_catch_up_hours = v_settings.report_catch_up_hours, alert_reminder_hours = v_settings.alert_reminder_hours,
        log_retention_days = v_settings.log_retention_days, gmail_address = v_settings.gmail_address,
        alert_recipients = v_settings.alert_recipients, report_recipients = v_settings.report_recipients,
        admin_recipients = v_settings.admin_recipients;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings));
end
$$;

-- Nowy czujnik z plikiem klucza z „pair” (dane Tuya od razu do Vault; MAC z pliku).
create function public.owner_sensor_add(p_version bigint, p_id text, p_name text, p_credentials jsonb, p_options jsonb)
returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_credentials jsonb;
    v_options     jsonb;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if p_id is null or p_id !~ '^[a-z0-9][a-z0-9_-]{0,39}$' then
        perform glebosaurus.fail('22023', 'czujnik: id – małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)');
    end if;
    if exists (select 1 from glebosaurus.sensors where id = p_id) then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ': id występuje więcej niż raz (jest już taki czujnik – także w archiwum)');
    end if;
    v_credentials := glebosaurus.normalize_credentials(p_credentials);
    v_options := glebosaurus.sensor_options(coalesce(p_options, '{}'), 'czujnik ' || p_id);
    perform glebosaurus.require_unique_device(p_id, v_credentials);

    insert into glebosaurus.sensors (id, name, mac, enabled, read_interval_minutes, watering_below_percent,
                                     watering_clear_above_percent, consecutive_readings, low_battery_percent, silent_after_hours)
    values (p_id, glebosaurus.sensor_name(p_name, p_id), v_credentials->>'mac', (v_options->>'enabled')::boolean,
            (v_options->>'readIntervalMinutes')::numeric, (v_options->>'wateringBelowPercent')::int,
            (v_options->>'wateringClearAbovePercent')::int, (v_options->>'consecutiveReadings')::int,
            (v_options->>'lowBatteryPercent')::int, (v_options->>'silentAfterHours')::numeric);
    perform glebosaurus.secret_put('glebosaurus:sensor:' || p_id, v_credentials::text);
    update glebosaurus.settings set updated_at = now();
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_credentials->>'mac');
end
$$;

-- Nazwa i ustawienia czujnika (id i MAC bez zmian). Czujnika z archiwum nie da się włączyć – najpierw przywrócenie.
create function public.owner_sensor_update(p_version bigint, p_id text, p_name text, p_options jsonb) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_sensor  glebosaurus.sensors;
    v_options jsonb;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    select * into v_sensor from glebosaurus.sensors where id = p_id;
    if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅')); end if;
    v_options := glebosaurus.sensor_options(coalesce(p_options, '{}'), 'czujnik ' || p_id);
    if v_sensor.archived_at is not null and (v_options->>'enabled')::boolean then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ' jest w archiwum – najpierw go przywróć');
    end if;
    update glebosaurus.sensors set
        name = glebosaurus.sensor_name(p_name, p_id), enabled = (v_options->>'enabled')::boolean,
        read_interval_minutes = (v_options->>'readIntervalMinutes')::numeric,
        watering_below_percent = (v_options->>'wateringBelowPercent')::int,
        watering_clear_above_percent = (v_options->>'wateringClearAbovePercent')::int,
        consecutive_readings = (v_options->>'consecutiveReadings')::int,
        low_battery_percent = (v_options->>'lowBatteryPercent')::int,
        silent_after_hours = (v_options->>'silentAfterHours')::numeric,
        updated_at = now()
     where id = p_id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings));
end
$$;

-- Nowy klucz czujnika (np. po resecie i ponownym „pair --id <to samo id>”); MAC z pliku zastępuje dotychczasowy.
create function public.owner_sensor_credentials(p_version bigint, p_id text, p_credentials jsonb) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_credentials jsonb;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if not exists (select 1 from glebosaurus.sensors where id = p_id) then
        perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅'));
    end if;
    v_credentials := glebosaurus.normalize_credentials(p_credentials);
    perform glebosaurus.require_unique_device(p_id, v_credentials);
    update glebosaurus.sensors set mac = v_credentials->>'mac', updated_at = now() where id = p_id;
    perform glebosaurus.secret_put('glebosaurus:sensor:' || p_id, v_credentials::text);
    update glebosaurus.settings set updated_at = now();
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_credentials->>'mac');
end
$$;

-- Archiwum: czujnik wyłączony i schowany (agenci go nie dostają, dzierżawa wygasa sama), odczyty i klucz zostają.
create function public.owner_sensor_archive(p_version bigint, p_id text, p_archive boolean) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if not exists (select 1 from glebosaurus.sensors where id = p_id) then
        perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅'));
    end if;
    if p_archive then
        update glebosaurus.sensors set enabled = false, archived_at = coalesce(archived_at, glebosaurus.clock()), updated_at = now()
         where id = p_id;
        -- Otwarte alerty czujnika z archiwum nie mają już kto zamknąć – zamykamy je od razu.
        update glebosaurus.alerts set resolved_at = glebosaurus.clock() where sensor_id = p_id and resolved_at is null;
    else
        -- Przywrócony czujnik wraca wyłączony – włącza go właściciel, gdy jest gotowy.
        update glebosaurus.sensors set archived_at = null, updated_at = now() where id = p_id;
    end if;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings));
end
$$;

-- Usunięcie na dobre – tylko czujnik bez żadnego odczytu (np. dodany przez pomyłkę); inaczej archiwum.
create function public.owner_sensor_delete(p_version bigint, p_id text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if not exists (select 1 from glebosaurus.sensors where id = p_id) then
        perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅'));
    end if;
    if exists (select 1 from glebosaurus.readings where sensor_id = p_id) then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ' ma odczyty w chmurze – można go tylko przenieść do archiwum');
    end if;
    delete from glebosaurus.sensors where id = p_id;
    delete from glebosaurus.leases where resource = 'sensor:' || p_id;
    delete from vault.secrets where name = 'glebosaurus:sensor:' || p_id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: sekrety (tylko ustawienie albo usunięcie – nigdy odczyt)
-- ---------------------------------------------------------------------------------------------------------------------

-- gmail-app-password (spacje z widoku Google są pomijane) albo healthchecks-ping-url (https); null – usunięcie.
create function public.owner_secret_set(p_name text, p_value text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_value text;
begin
    perform glebosaurus.require_owner();
    if p_name not in ('gmail-app-password', 'healthchecks-ping-url') then
        perform glebosaurus.fail('22023', 'nieznany sekret ' || coalesce(p_name, '∅'));
    end if;
    if p_value is null then
        delete from vault.secrets where name = 'glebosaurus:' || p_name;
        return jsonb_build_object('set', false);
    end if;
    if p_name = 'gmail-app-password' then
        v_value := replace(trim(p_value), ' ', '');
        if length(v_value) not between 8 and 100 or v_value ~ '[[:space:]]' then
            perform glebosaurus.fail('22023', 'hasło aplikacji Gmail: 16 liter z myaccount.google.com/apppasswords (spacje są pomijane)');
        end if;
    else
        v_value := trim(p_value);
        if v_value !~ '^https://[^[:space:]/]+/[^[:space:]]*$' or length(v_value) > 300 then
            perform glebosaurus.fail('22023', 'adres sygnału: pełny adres https:// z healthchecks.io (Ping URL)');
        end if;
    end if;
    perform glebosaurus.secret_put('glebosaurus:' || p_name, v_value);
    return jsonb_build_object('set', true, 'setAt', (select updated_at from vault.secrets where name = 'glebosaurus:' || p_name));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: agenci
-- ---------------------------------------------------------------------------------------------------------------------

-- Nowy agent albo zmiana roli i włączenia (agent przejmuje rolę przy najbliższej synchronizacji, bez ponownej rejestracji).
create function public.owner_agent_save(p_name text, p_can_send_mail boolean, p_sender_priority int, p_enabled boolean)
returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    if p_name is null or p_name !~ '^[a-z0-9][a-z0-9_-]{0,39}$' then
        perform glebosaurus.fail('22023', 'nazwa agenta: małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)');
    end if;
    if coalesce(p_can_send_mail, false) and (p_sender_priority is null or p_sender_priority not between 1 and 9) then
        perform glebosaurus.fail('22023', 'priorytet nadawcy: od 1 (najważniejszy) do 9');
    end if;
    perform glebosaurus.admin_agent_upsert(p_name, coalesce(p_can_send_mail, false),
        case when coalesce(p_can_send_mail, false) then p_sender_priority end, coalesce(p_enabled, true));
    return jsonb_build_object('name', p_name);
end
$$;

-- Jednorazowy kod rejestracji (24 h; pokazywany tylko raz). Rejestracja zastąpi obecny token agenta.
create function public.owner_agent_code(p_name text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_code text;
begin
    perform glebosaurus.require_owner();
    v_code := glebosaurus.admin_enrollment_code(p_name, interval '24 hours');
    return jsonb_build_object('code', v_code, 'expiresAt', glebosaurus.clock() + interval '24 hours');
end
$$;

-- Odcięcie agenta: token przestaje działać od razu, niewykorzystane kody przepadają (jak cloud agent --revoke).
create function public.owner_agent_revoke(p_name text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent  glebosaurus.agents;
    v_codes  int;
begin
    perform glebosaurus.require_owner();
    select * into v_agent from glebosaurus.agents where name = p_name for update;
    if not found then perform glebosaurus.fail('22023', 'nie ma agenta ' || coalesce(p_name, '∅')); end if;
    perform glebosaurus.admin_agent_revoke(p_name);
    delete from glebosaurus.enrollment_codes
     where agent_id = v_agent.id and used_at is null and expires_at > glebosaurus.clock();
    get diagnostics v_codes = row_count;
    return jsonb_build_object('hadToken', v_agent.token_hash is not null, 'cancelledCodes', v_codes);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: polecenia i ich stan
-- ---------------------------------------------------------------------------------------------------------------------

-- read_now (czujnik), report_now i mail_test (do obecnego nadawcy – PRAWDZIWY mail), send_log (agent).
create function public.owner_command(p_kind text, p_sensor text default null, p_agent text default null) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent_id int;
begin
    perform glebosaurus.require_owner();
    if p_kind is null or p_kind not in ('read_now', 'report_now', 'mail_test', 'send_log') then
        perform glebosaurus.fail('22023', 'strona zleca tylko read_now, report_now, mail_test i send_log');
    end if;
    if p_agent is not null then
        select id into v_agent_id from glebosaurus.agents where name = p_agent;
        if v_agent_id is null then perform glebosaurus.fail('22023', 'nie ma agenta ' || p_agent); end if;
    end if;
    if p_kind = 'read_now' and exists (select 1 from glebosaurus.sensors where id = p_sensor and not enabled) then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor || ' jest wyłączony');
    end if;
    return glebosaurus.command_create(p_kind, p_sensor, v_agent_id, '{}', 'owner-web');
end
$$;

-- Stan poleceń (najwyżej 50): kto wziął, kiedy skończył, wynik; wygasłe bez wyniku – „expired”.
create function public.owner_command_status(p_ids bigint[]) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    return coalesce((
        select jsonb_agg(jsonb_build_object(
            'id', c.id, 'kind', c.kind, 'sensor', c.sensor_id, 'createdAt', c.created_at, 'expiresAt', c.expires_at,
            'takenBy', a.name, 'takenAt', c.taken_at, 'doneAt', c.done_at, 'result', c.result,
            'expired', c.done_at is null and c.expires_at <= v_now) order by c.id)
          from glebosaurus.commands c left join glebosaurus.agents a on a.id = c.taken_by
         where c.id = any(p_ids[1:50])), '[]'::jsonb);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Pulpit bez czujników z archiwum (jak w 20261121120000_owner_web.sql; zmiana: archived_at is null)
-- ---------------------------------------------------------------------------------------------------------------------

create or replace function public.owner_dashboard() returns jsonb
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
              left join glebosaurus.agents ha on ha.id = l.holder_id
             where s.archived_at is null), '[]'::jsonb),
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
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.owner_config() from public, anon, authenticated, service_role;
revoke all on function public.owner_settings_update(bigint, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_add(bigint, text, text, jsonb, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_update(bigint, text, text, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_credentials(bigint, text, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_archive(bigint, text, boolean) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_delete(bigint, text) from public, anon, authenticated, service_role;
revoke all on function public.owner_secret_set(text, text) from public, anon, authenticated, service_role;
revoke all on function public.owner_agent_save(text, boolean, int, boolean) from public, anon, authenticated, service_role;
revoke all on function public.owner_agent_code(text) from public, anon, authenticated, service_role;
revoke all on function public.owner_agent_revoke(text) from public, anon, authenticated, service_role;
revoke all on function public.owner_command(text, text, text) from public, anon, authenticated, service_role;
revoke all on function public.owner_command_status(bigint[]) from public, anon, authenticated, service_role;

grant execute on function public.owner_config() to authenticated;
grant execute on function public.owner_settings_update(bigint, jsonb) to authenticated;
grant execute on function public.owner_sensor_add(bigint, text, text, jsonb, jsonb) to authenticated;
grant execute on function public.owner_sensor_update(bigint, text, text, jsonb) to authenticated;
grant execute on function public.owner_sensor_credentials(bigint, text, jsonb) to authenticated;
grant execute on function public.owner_sensor_archive(bigint, text, boolean) to authenticated;
grant execute on function public.owner_sensor_delete(bigint, text) to authenticated;
grant execute on function public.owner_secret_set(text, text) to authenticated;
grant execute on function public.owner_agent_save(text, boolean, int, boolean) to authenticated;
grant execute on function public.owner_agent_code(text) to authenticated;
grant execute on function public.owner_agent_revoke(text) to authenticated;
grant execute on function public.owner_command(text, text, text) to authenticated;
grant execute on function public.owner_command_status(bigint[]) to authenticated;

comment on function public.owner_config() is 'glebosaurus-api';
comment on function public.owner_settings_update(bigint, jsonb) is 'glebosaurus-api';
comment on function public.owner_sensor_add(bigint, text, text, jsonb, jsonb) is 'glebosaurus-api';
comment on function public.owner_sensor_update(bigint, text, text, jsonb) is 'glebosaurus-api';
comment on function public.owner_sensor_credentials(bigint, text, jsonb) is 'glebosaurus-api';
comment on function public.owner_sensor_archive(bigint, text, boolean) is 'glebosaurus-api';
comment on function public.owner_sensor_delete(bigint, text) is 'glebosaurus-api';
comment on function public.owner_secret_set(text, text) is 'glebosaurus-api';
comment on function public.owner_agent_save(text, boolean, int, boolean) is 'glebosaurus-api';
comment on function public.owner_agent_code(text) is 'glebosaurus-api';
comment on function public.owner_agent_revoke(text) is 'glebosaurus-api';
comment on function public.owner_command(text, text, text) is 'glebosaurus-api';
comment on function public.owner_command_status(bigint[]) is 'glebosaurus-api';
