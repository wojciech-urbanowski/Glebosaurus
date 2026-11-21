-- Faza 3, poprawka: UPDATE bez WHERE w funkcjach wywoływanych przez Data API.
--
-- Supabase ładuje rozszerzenie pg_safeupdate dla roli authenticator (session_preload_libraries = supautils, safeupdate),
-- więc każde żądanie przez Data API (strona właściciela, agenci) odrzuca UPDATE i DELETE bez WHERE – także wewnątrz
-- funkcji i wyzwalaczy („UPDATE requires a WHERE clause”, SQLSTATE 21000). Testy SQL i narzędzia łączą się z bazą wprost,
-- bez tego rozszerzenia, więc tego nie widziały: na prawdziwej stronie zawodziły wyciszenia, zapis ustawień i każda zmiana
-- czujnika (wyzwalacz config_changed). Tabela settings ma zawsze jeden wiersz (id = true), stąd „where id”. Treść funkcji
-- bez zmian poza tym warunkiem; funkcje admin_* (tylko połączenie bezpośrednie) poprawione dla jednej, prostej reguły –
-- pilnuje jej test statyczny SqlSafeUpdateTests.

create or replace function glebosaurus.config_changed() returns trigger
language plpgsql set search_path = '' as $$
begin
    update glebosaurus.settings set updated_at = now() where id;
    return null;
end
$$;

create or replace function glebosaurus.admin_settings_update(p jsonb) returns void
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
            then array(select jsonb_array_elements_text(p->'adminRecipients')) else admin_recipients end where id;
end
$$;

create or replace function glebosaurus.admin_sensor_credentials(p_sensor_id text, p_credentials jsonb) returns void
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
    update glebosaurus.settings set updated_at = now() where id;
end
$$;

create or replace function public.owner_settings_update(p_version bigint, p jsonb) returns jsonb
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
        admin_recipients = v_settings.admin_recipients where id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings));
end
$$;

create or replace function public.owner_sensor_add(p_version bigint, p_id text, p_name text, p_credentials jsonb, p_options jsonb)
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
    update glebosaurus.settings set updated_at = now() where id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_credentials->>'mac');
end
$$;

create or replace function public.owner_sensor_credentials(p_version bigint, p_id text, p_credentials jsonb) returns jsonb
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
    update glebosaurus.settings set updated_at = now() where id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_credentials->>'mac');
end
$$;

create or replace function public.owner_alerts_mute(p_sensor text, p_until timestamptz) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    if p_until is not null and (p_until <= v_now or p_until > v_now + interval '90 days') then
        perform glebosaurus.fail('22023', 'wyciszenie: od teraz do najwyżej 90 dni naprzód');
    end if;
    if p_sensor is null then
        update glebosaurus.settings set alerts_muted_until = p_until where id;
    else
        update glebosaurus.sensors set alerts_muted_until = p_until, updated_at = now() where id = p_sensor;
        if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || p_sensor); end if;
    end if;
    return jsonb_build_object('sensor', p_sensor, 'alertsMutedUntil', p_until,
                              'configVersion', (select config_version from glebosaurus.settings));
end
$$;

create or replace function public.pair_complete(p_token text, p_id bigint, p_result jsonb) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent       glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_command     glebosaurus.commands := glebosaurus.pair_command(v_agent, p_id);
    v_sensor      text := v_command.params->>'sensor';
    v_credentials jsonb := glebosaurus.secret_get('glebosaurus:pairing:' || v_sensor)::jsonb;
    v_created     boolean;
begin
    if v_credentials is null then perform glebosaurus.fail('22023', 'parowanie bez zapisanego klucza (pair_key_put)'); end if;
    perform glebosaurus.require_unique_device(v_sensor, v_credentials);
    v_created := not exists (select 1 from glebosaurus.sensors where id = v_sensor);
    if v_created then
        insert into glebosaurus.sensors (id, name, mac)
        values (v_sensor, glebosaurus.sensor_name(v_command.params->>'name', v_sensor), v_credentials->>'mac');
    else
        update glebosaurus.sensors set mac = v_credentials->>'mac', updated_at = now() where id = v_sensor;
    end if;
    perform glebosaurus.secret_put('glebosaurus:sensor:' || v_sensor, v_credentials::text);
    delete from vault.secrets where name = 'glebosaurus:pairing:' || v_sensor;
    update glebosaurus.settings set updated_at = now() where id;
    update glebosaurus.commands set done_at = glebosaurus.clock(),
           result = (case when jsonb_typeof(p_result) = 'object' and length(p_result::text) <= 3000 then p_result else '{}'::jsonb end)
                    || jsonb_build_object('ok', true, 'created', v_created, 'mac', v_credentials->>'mac')
     where id = p_id;
    return jsonb_build_object('sensor', v_sensor, 'created', v_created, 'mac', v_credentials->>'mac');
end
$$;

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;
