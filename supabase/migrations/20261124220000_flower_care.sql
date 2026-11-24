-- Glebosaurus 0.4.0 – czujnik Xiaomi / HHCC Flower Care (HHCCJCY01) obok czujników Tuya (docs/ARCHITEKTURA.md, zasady 5, 8
-- i 13): rodzaj czujnika (sensors.model), światło i żyzność w odczytach (readings.light_lux, readings.conductivity).
-- Czujnik Flower Care nie ma klucza – w Vault nic nie trzyma, rozpoznaje się go po adresie MAC; mierzy też światło (lx) i
-- przewodność gleby („żyzność”, µS/cm), a temperaturę w główce nad ziemią. Protokół agentów zmienia się tylko przez
-- dodanie pól (zasada 10): starszy agent pomija czujnik bez danych Tuya z ostrzeżeniem, a w odczytach nowych pól nie wysyła.
-- Funkcje zastąpione w całości opisują zmianę w komentarzu nad sobą.

alter table glebosaurus.sensors
    add column model text not null default 'tuya' check (model in ('tuya', 'flower_care'));

alter table glebosaurus.readings
    add column light_lux    int      check (light_lux between 0 and 200000),
    add column conductivity smallint check (conductivity between 0 and 20000);

-- Adres MAC w postaci AA:BB:CC:DD:EE:FF (wejście także z „-” albo bez separatorów, małymi literami); null – zły zapis.
create function glebosaurus.normalize_mac(p_mac text) returns text
language sql immutable set search_path = '' as $$
    select case when v ~ '^[0-9A-F]{12}$' then
                substr(v, 1, 2) || ':' || substr(v, 3, 2) || ':' || substr(v, 5, 2) || ':' ||
                substr(v, 7, 2) || ':' || substr(v, 9, 2) || ':' || substr(v, 11, 2) end
      from (select regexp_replace(upper(trim(coalesce(p_mac, ''))), '[:-]', '', 'g') as v) x
$$;

-- Światło i żyzność odczytu jako dodatek do obiektu JSON – tylko gdy są (odczyty Tuya ich nie mają, a transfer darmowego
-- planu jest ograniczony).
create function glebosaurus.reading_extras(p_light int, p_conductivity int) returns jsonb
language sql immutable set search_path = '' as $$
    select jsonb_strip_nulls(jsonb_build_object('light', p_light, 'conductivity', p_conductivity))
$$;

-- Jak w 20261121140000_owner_config.sql; zmiana: dane Tuya (z UUID) dla istniejącego czujnika Flower Care – odmowa (klucz
-- z pliku „pair”, nowy klucz w edytorze i parowanie zdalne dotyczą tylko czujników Tuya).
create or replace function glebosaurus.require_unique_device(p_sensor_id text, p_credentials jsonb) returns void
language plpgsql set search_path = '' as $$
declare
    v_other text;
begin
    if p_credentials ? 'uuid' and exists (select 1 from glebosaurus.sensors where id = p_sensor_id and model = 'flower_care') then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor_id || ' to Xiaomi Flower Care – nie ma klucza Tuya (zmiana adresu: „Nowy adres MAC”)');
    end if;
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
-- API agentów
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261119120000_agent_device_status.sql; zmiana: odczyty z polami light i conductivity (zakresy jak w tabeli).
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
               r.light, r.conductivity,
               case
                   when r.sensor is null or r.at is null then 'missing'
                   when not exists (select 1 from glebosaurus.sensors s where s.id = r.sensor) then 'unknown_sensor'
                   when r.at > v_now + interval '10 minutes' then 'future'
                   when r.at < timestamptz '2020-01-01' then 'too_old'
                   when r.moisture not between 0 and 100 or r."tempTenths" not between -400 and 1000
                     or r.battery not between 0 and 100 or r."batteryState" not between 0 and 9
                     or r.light not between 0 and 200000 or r.conductivity not between 0 and 20000 then 'out_of_range'
               end as reason
          from jsonb_to_recordset(v_readings) as r(sensor text, at timestamptz, moisture int, "tempTenths" int,
                                                   battery int, "batteryState" int, light bigint, conductivity bigint)),
    inserted as (
        insert into glebosaurus.readings (sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity, agent_id)
        select sensor, at, moisture, temp_tenths, battery, battery_state, light, conductivity, v_agent.id from source where reason is null
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
                                                'lastSeenSecondsAgo', glebosaurus.seconds(v_now - a.last_seen_at),
                                                'status', glebosaurus.agent_status_view(a.last_status))
                             order by a.name)
              from glebosaurus.agents a where a.enabled and a.token_hash is not null), '[]'::jsonb));
end
$$;

-- Jak w 20261121180000_owner_alerts.sql; zmiana: rodzaj czujnika (model) – czujnik Flower Care nie ma danych Tuya.
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
            'logRetentionDays', v_settings.log_retention_days,
            'alertsMutedUntil', v_settings.alerts_muted_until),
        'mail', v_mail,
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'model', s.model, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', s.read_interval_minutes,
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'consecutiveReadings', s.consecutive_readings,
                'lowBatteryPercent', s.low_battery_percent,
                'silentAfterHours', s.silent_after_hours,
                'alertsMutedUntil', s.alerts_muted_until,
                'tuya', case when s.model = 'tuya' then glebosaurus.secret_get('glebosaurus:sensor:' || s.id)::jsonb end) order by s.id)
            from glebosaurus.sensors s where s.archived_at is null), '[]'::jsonb));
end
$$;

-- Jak w 20261116200000_core.sql; zmiana: światło i żyzność (tylko gdy są).
create or replace function public.readings_range(p_token text, p_sensors text[], p_from timestamptz, p_to timestamptz) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.agent_auth(p_token);
    if p_from is null or p_to is null or p_to < p_from or p_to - p_from > interval '400 days' then
        perform glebosaurus.fail('22023', 'przedział odczytów musi mieć od 0 do 400 dni');
    end if;
    return coalesce((
        select jsonb_agg(jsonb_build_object('sensor', sensor_id, 'at', at, 'moisture', moisture,
                                            'tempTenths', temp_tenths, 'battery', battery, 'batteryState', battery_state)
                         || glebosaurus.reading_extras(light_lux, conductivity)
                         order by sensor_id, at)
          from (select * from glebosaurus.readings
                 where sensor_id = any(p_sensors) and at between p_from and p_to
                 order by sensor_id, at limit 50000) r), '[]'::jsonb);
end
$$;

-- Jak w 20261117120000_readings_feed.sql; zmiana: światło i żyzność (tylko gdy są).
create or replace function public.readings_feed(p_token text, p_after bigint, p_limit int default 5000) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_settled  timestamptz;
    v_barrier  bigint;
    v_readings jsonb;
    v_last     bigint;
    v_count    int;
begin
    perform glebosaurus.agent_auth(p_token);
    if p_after is null or p_after < 0 or p_limit is null or p_limit not between 1 and 10000 then
        perform glebosaurus.fail('22023', 'p_after musi być ≥ 0, a p_limit od 1 do 10000');
    end if;
    v_settled := glebosaurus.clock() - interval '30 seconds';
    select min(seq) into v_barrier from glebosaurus.readings where received_at > v_settled and seq > p_after;

    select coalesce(jsonb_agg(jsonb_build_object('sensor', r.sensor_id, 'at', r.at, 'moisture', r.moisture,
                                                 'tempTenths', r.temp_tenths, 'battery', r.battery,
                                                 'batteryState', r.battery_state)
                              || glebosaurus.reading_extras(r.light_lux, r.conductivity) order by r.seq), '[]'::jsonb),
           max(r.seq), count(*)
      into v_readings, v_last, v_count
      from (select * from glebosaurus.readings
             where seq > p_after and (v_barrier is null or seq < v_barrier)
             order by seq limit p_limit) r;

    return jsonb_build_object('readings', v_readings, 'last', coalesce(v_last, p_after), 'more', v_count = p_limit);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: odczyty i wykresy
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261121160000_owner_history.sql; zmiana: światło i żyzność (średnia do 1, min, max – jak ReadingSeriesBuilder;
-- klucze tylko, gdy w punkcie są takie odczyty).
create or replace function glebosaurus.series(p_sensor text, p_from timestamptz, p_to timestamptz, p_points int, p_zone text,
    p_interval_minutes numeric) returns jsonb
language plpgsql stable set search_path = '' set timezone = 'UTC' as $$
declare
    v_points int := least(greatest(coalesce(p_points, 300), 10), 2000);
    v_count  bigint;
    v_bucket int;
    v_gap    interval;
    v_result jsonb;
begin
    select count(*) into v_count from glebosaurus.readings where sensor_id = p_sensor and at between p_from and p_to;
    v_bucket := glebosaurus.series_bucket(v_count, p_to - p_from, v_points);
    v_gap := make_interval(mins => v_bucket) + 3 * make_interval(secs => (p_interval_minutes * 60)::double precision);

    with bucketed as (
        select r.at, r.moisture, r.temp_tenths, r.light_lux, r.conductivity,
               case when v_bucket = 0 then r.at
                    else date_bin(make_interval(mins => v_bucket), r.at at time zone p_zone, timestamp '0001-01-01 00:00:00')
                             at time zone 'UTC' end as bucket
          from glebosaurus.readings r
         where r.sensor_id = p_sensor and r.at between p_from and p_to
    ), readings as (
        select *, min(at) over (partition by bucket) as first_at from bucketed
    ), grouped as (
        -- Chwila punktu: pierwszy odczyt przedziału plus średnie przesunięcie (jak w C# – bez dużych liczb).
        select bucket, count(*) as n, min(at) + avg(at - first_at) as at,
               round(avg(moisture), 1) as moisture, min(moisture) as moisture_min, max(moisture) as moisture_max,
               round(avg(temp_tenths) / 10.0, 2) as temperature, round(min(temp_tenths) / 10.0, 1) as temperature_min,
               round(max(temp_tenths) / 10.0, 1) as temperature_max,
               round(avg(light_lux)) as light, min(light_lux) as light_min, max(light_lux) as light_max,
               round(avg(conductivity)) as conductivity, min(conductivity) as conductivity_min, max(conductivity) as conductivity_max
          from readings group by bucket
    ), ordered as (
        select *, lag(at) over (order by at) as previous from grouped
    )
    select coalesce(jsonb_agg(jsonb_build_object(
               'at', date_trunc('milliseconds', at), 'count', n,
               'moisture', moisture, 'moistureMin', moisture_min, 'moistureMax', moisture_max,
               'temperature', temperature, 'temperatureMin', temperature_min, 'temperatureMax', temperature_max,
               'gapBefore', previous is not null and at - previous > v_gap)
               || jsonb_strip_nulls(jsonb_build_object(
                      'light', light, 'lightMin', light_min, 'lightMax', light_max,
                      'conductivity', conductivity, 'conductivityMin', conductivity_min, 'conductivityMax', conductivity_max))
               order by at), '[]'::jsonb)
      into v_result from ordered;

    return jsonb_build_object(
        'sensorId', p_sensor, 'from', p_from, 'to', p_to, 'bucketMinutes', v_bucket,
        'firstAt', (select min(at) from glebosaurus.readings where sensor_id = p_sensor),
        'lastAt', (select max(at) from glebosaurus.readings where sensor_id = p_sensor),
        'points', v_result);
end
$$;

-- Jak w 20261121160000_owner_history.sql; zmiana: światło i żyzność (tylko gdy są).
create or replace function public.owner_readings(p_id text, p_count int) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    return coalesce((
        select jsonb_agg(jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                            'battery', r.battery, 'batteryState', r.battery_state, 'agent', r.agent)
                         || glebosaurus.reading_extras(r.light_lux, r.conductivity) order by r.at desc)
          from (select x.at, x.moisture, x.temp_tenths, x.battery, x.battery_state, x.light_lux, x.conductivity, a.name as agent
                  from glebosaurus.readings x left join glebosaurus.agents a on a.id = x.agent_id
                 where x.sensor_id = p_id order by x.at desc limit least(greatest(coalesce(p_count, 96), 1), 1000)) r), '[]'::jsonb);
end
$$;

-- Jak w 20261121160000_owner_history.sql; zmiany: rodzaj czujnika (model), produkt Tuya tylko dla czujnika Tuya, światło
-- i żyzność w ostatnim odczycie.
create or replace function public.owner_sensor(p_id text) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now    timestamptz;
    v_sensor glebosaurus.sensors;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    select * into v_sensor from glebosaurus.sensors where id = p_id;
    if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅')); end if;
    return jsonb_build_object(
        'serverTime', v_now,
        'sensor', jsonb_build_object(
            'id', v_sensor.id, 'name', v_sensor.name, 'model', v_sensor.model, 'mac', v_sensor.mac, 'enabled', v_sensor.enabled,
            'archivedAt', v_sensor.archived_at,
            'readIntervalMinutes', coalesce(v_sensor.read_interval_minutes, (select read_interval_minutes from glebosaurus.settings)),
            'ownReadInterval', v_sensor.read_interval_minutes is not null,
            'wateringBelowPercent', v_sensor.watering_below_percent,
            'wateringClearAbovePercent', v_sensor.watering_clear_above_percent,
            'consecutiveReadings', v_sensor.consecutive_readings,
            'lowBatteryPercent', v_sensor.low_battery_percent,
            'silentAfterHours', v_sensor.silent_after_hours,
            'productId', case when v_sensor.model = 'tuya'
                              then glebosaurus.secret_get('glebosaurus:sensor:' || v_sensor.id)::jsonb->>'productId' end),
        'last', (select jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                          'battery', r.battery, 'batteryState', r.battery_state, 'agent', a.name)
                        || glebosaurus.reading_extras(r.light_lux, r.conductivity)
                   from glebosaurus.readings r left join glebosaurus.agents a on a.id = r.agent_id
                  where r.sensor_id = p_id order by r.at desc limit 1),
        'firstAt', (select min(at) from glebosaurus.readings where sensor_id = p_id),
        'holder', (select jsonb_build_object('agent', a.name, 'expiresAt', l.expires_at)
                     from glebosaurus.leases l join glebosaurus.agents a on a.id = l.holder_id
                    where l.resource = 'sensor:' || p_id and l.expires_at > v_now),
        'sightings', coalesce((
            select jsonb_agg(jsonb_build_object('agent', a.name, 'rssi', g.rssi, 'heardAt', g.heard_at,
                                                'lastAttemptAt', g.last_attempt_at, 'lastSuccessAt', g.last_success_at,
                                                'failures', g.consecutive_failures) order by g.rssi desc nulls last, a.name)
              from glebosaurus.sightings g join glebosaurus.agents a on a.id = g.agent_id
             where g.sensor_id = p_id and a.enabled), '[]'::jsonb),
        'alerts', coalesce((
            select jsonb_agg(glebosaurus.alert_json(al) order by al.opened_at)
              from glebosaurus.alerts al where al.sensor_id = p_id and al.resolved_at is null), '[]'::jsonb),
        'others', coalesce((
            select jsonb_agg(jsonb_build_object('id', s.id, 'name', s.name) order by s.name, s.id)
              from glebosaurus.sensors s where s.archived_at is null), '[]'::jsonb));
end
$$;

-- Jak w 20261121180000_owner_alerts.sql; zmiany: rodzaj czujnika (model), światło i żyzność w ostatnim odczycie.
create or replace function public.owner_dashboard() returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now      timestamptz;
    v_settings glebosaurus.settings;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    select * into v_settings from glebosaurus.settings;
    return jsonb_build_object(
        'serverTime', v_now,
        'configVersion', v_settings.config_version,
        'timeZone', v_settings.time_zone,
        'reportTimes', to_jsonb(v_settings.report_times),
        'reportRecipients', to_jsonb(case when cardinality(v_settings.report_recipients) > 0 then v_settings.report_recipients
                                          else v_settings.alert_recipients end),
        'alertsMutedUntil', case when v_settings.alerts_muted_until > v_now then v_settings.alerts_muted_until end,
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'model', s.model, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', coalesce(s.read_interval_minutes, v_settings.read_interval_minutes),
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'lowBatteryPercent', s.low_battery_percent,
                'alertsMutedUntil', case when s.alerts_muted_until > v_now then s.alerts_muted_until end,
                'last', (select jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                                   'battery', r.battery, 'batteryState', r.battery_state, 'agent', ra.name)
                                || glebosaurus.reading_extras(r.light_lux, r.conductivity)
                           from glebosaurus.readings r left join glebosaurus.agents ra on ra.id = r.agent_id
                          where r.sensor_id = s.id order by r.at desc limit 1),
                'holder', case when l.expires_at > v_now then ha.name end) order by s.name, s.id)
              from glebosaurus.sensors s
              left join glebosaurus.leases l on l.resource = 'sensor:' || s.id
              left join glebosaurus.agents ha on ha.id = l.holder_id
             where s.archived_at is null), '[]'::jsonb),
        'alerts', coalesce((
            select jsonb_agg(glebosaurus.alert_json(a) || jsonb_build_object('sensorName', s.name) order by a.opened_at)
              from glebosaurus.alerts a join glebosaurus.sensors s on s.id = a.sensor_id
             where a.resolved_at is null and s.archived_at is null), '[]'::jsonb),
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
-- API strony: konfiguracja
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261121140000_owner_config.sql; zmiana: rodzaj czujnika (model).
create or replace function public.owner_config() returns jsonb
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
                'id', s.id, 'name', s.name, 'model', s.model, 'mac', s.mac, 'enabled', s.enabled, 'archivedAt', s.archived_at,
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

-- Nowy czujnik Xiaomi Flower Care: tylko adres MAC (podaje go „glebosaurus scan”), bez klucza i bez Vault. Ustawienia i
-- walidacja jak w owner_sensor_add.
create function public.owner_sensor_add_flower_care(p_version bigint, p_id text, p_name text, p_mac text, p_options jsonb)
returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_mac     text := glebosaurus.normalize_mac(p_mac);
    v_options jsonb;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    if p_id is null or p_id !~ '^[a-z0-9][a-z0-9_-]{0,39}$' then
        perform glebosaurus.fail('22023', 'czujnik: id – małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)');
    end if;
    if exists (select 1 from glebosaurus.sensors where id = p_id) then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ': id występuje więcej niż raz (jest już taki czujnik – także w archiwum)');
    end if;
    if v_mac is null then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ': adres MAC, np. 5C:85:7E:14:78:3C (podaje go glebosaurus scan)');
    end if;
    v_options := glebosaurus.sensor_options(coalesce(p_options, '{}'), 'czujnik ' || p_id);
    perform glebosaurus.require_unique_device(p_id, jsonb_build_object('mac', v_mac));

    insert into glebosaurus.sensors (id, name, model, mac, enabled, read_interval_minutes, watering_below_percent,
                                     watering_clear_above_percent, consecutive_readings, low_battery_percent, silent_after_hours)
    values (p_id, glebosaurus.sensor_name(p_name, p_id), 'flower_care', v_mac, (v_options->>'enabled')::boolean,
            (v_options->>'readIntervalMinutes')::numeric, (v_options->>'wateringBelowPercent')::int,
            (v_options->>'wateringClearAbovePercent')::int, (v_options->>'consecutiveReadings')::int,
            (v_options->>'lowBatteryPercent')::int, (v_options->>'silentAfterHours')::numeric);
    update glebosaurus.settings set updated_at = now() where id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_mac);
end
$$;

-- Nowy adres czujnika Flower Care (wymiana czujnika przy tej samej roślinie – historia zostaje pod tym samym id).
create function public.owner_sensor_mac(p_version bigint, p_id text, p_mac text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_mac    text := glebosaurus.normalize_mac(p_mac);
    v_sensor glebosaurus.sensors;
begin
    perform glebosaurus.require_owner();
    perform glebosaurus.require_config_version(p_version);
    select * into v_sensor from glebosaurus.sensors where id = p_id;
    if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_id, '∅')); end if;
    if v_sensor.model <> 'flower_care' then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ' to czujnik Tuya – adres zmienia nowy klucz (plik z „pair” albo parowanie zdalne)');
    end if;
    if v_mac is null then
        perform glebosaurus.fail('22023', 'czujnik ' || p_id || ': adres MAC, np. 5C:85:7E:14:78:3C (podaje go glebosaurus scan)');
    end if;
    perform glebosaurus.require_unique_device(p_id, jsonb_build_object('mac', v_mac));
    update glebosaurus.sensors set mac = v_mac, updated_at = now() where id = p_id;
    update glebosaurus.settings set updated_at = now() where id;
    return jsonb_build_object('configVersion', (select config_version from glebosaurus.settings), 'mac', v_mac);
end
$$;

-- Jak w 20261121220000_owner_hardening.sql; zmiana: id czujnika Flower Care – odmowa od razu (nie ma czego parować), zanim
-- właściciel włoży baterie do czujnika Tuya.
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
    if exists (select 1 from glebosaurus.sensors where id = p_sensor and model = 'flower_care') then
        perform glebosaurus.fail('22023', 'czujnik ' || p_sensor || ' to Xiaomi Flower Care – nie wymaga parowania');
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

revoke all on function public.owner_sensor_add_flower_care(bigint, text, text, text, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor_mac(bigint, text, text) from public, anon, authenticated, service_role;
grant execute on function public.owner_sensor_add_flower_care(bigint, text, text, text, jsonb) to authenticated;
grant execute on function public.owner_sensor_mac(bigint, text, text) to authenticated;
comment on function public.owner_sensor_add_flower_care(bigint, text, text, text, jsonb) is 'glebosaurus-api';
comment on function public.owner_sensor_mac(bigint, text, text) is 'glebosaurus-api';

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;
