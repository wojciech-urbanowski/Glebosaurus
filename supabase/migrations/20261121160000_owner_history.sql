-- Glebosaurus, faza 3 (etap 3, 3a) – pulpit i historia na stronie właściciela (docs/ARCHITEKTURA.md, zasady 10 i 11):
-- dane wykresów uśredniane w bazie tak samo jak ReadingSeriesBuilder lokalnego panelu (ta sama drabinka przedziałów,
-- przedziały w czasie lokalnym od poniedziałku 0001-01-01, ta sama reguła dziur – pilnuje SeriesContractTests), strona
-- czujnika (stan, słyszenia, alerty, ostatnie odczyty) i otwarte alerty na pulpicie. Same funkcje – bez zmian schematu.

-- ---------------------------------------------------------------------------------------------------------------------
-- Serie do wykresów
-- ---------------------------------------------------------------------------------------------------------------------

-- Najkrótszy przedział z drabinki, przy którym okno mieści się w p_points punktach; 0 = odczyty wprost.
create function glebosaurus.series_bucket(p_count bigint, p_window interval, p_points int) returns int
language sql immutable set search_path = '' as $$
    select case
        when p_count <= p_points then 0
        else coalesce((select m from unnest(array[30, 60, 120, 180, 360, 720, 1440, 2880, 10080, 20160, 40320]) with ordinality as l(m, i)
                        where extract(epoch from p_window) / 60.0 / m <= p_points order by i limit 1), 40320)
    end
$$;

-- Seria czujnika z okna [p_from, p_to] (granice włącznie) jak ReadingSeriesBuilder.Build: punkty to pojedyncze odczyty
-- albo przedziały czasu lokalnego (p_zone) ze średnią (wilgotność do 0,1, temperatura do 0,01), min i max; gapBefore,
-- gdy od poprzedniego punktu minęło więcej niż przedział plus 3 odstępy odczytu.
create function glebosaurus.series(p_sensor text, p_from timestamptz, p_to timestamptz, p_points int, p_zone text,
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
        select r.at, r.moisture, r.temp_tenths,
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
               round(max(temp_tenths) / 10.0, 1) as temperature_max
          from readings group by bucket
    ), ordered as (
        select *, lag(at) over (order by at) as previous from grouped
    )
    select coalesce(jsonb_agg(jsonb_build_object(
               'at', date_trunc('milliseconds', at), 'count', n,
               'moisture', moisture, 'moistureMin', moisture_min, 'moistureMax', moisture_max,
               'temperature', temperature, 'temperatureMin', temperature_min, 'temperatureMax', temperature_max,
               'gapBefore', previous is not null and at - previous > v_gap) order by at), '[]'::jsonb)
      into v_result from ordered;

    return jsonb_build_object(
        'sensorId', p_sensor, 'from', p_from, 'to', p_to, 'bucketMinutes', v_bucket,
        'firstAt', (select min(at) from glebosaurus.readings where sensor_id = p_sensor),
        'lastAt', (select max(at) from glebosaurus.readings where sensor_id = p_sensor),
        'points', v_result);
end
$$;

-- Wykres na stronie: okno do 20 lat, strefa przeglądarki (nazwa IANA) albo – bez niej – strefa raportów.
create function public.owner_series(p_sensor text, p_from timestamptz, p_to timestamptz, p_points int, p_zone text default null)
returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_sensor glebosaurus.sensors;
    v_zone   text;
begin
    perform glebosaurus.require_owner();
    select * into v_sensor from glebosaurus.sensors where id = p_sensor;
    if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || coalesce(p_sensor, '∅')); end if;
    if p_from is null or p_to is null or p_to <= p_from or p_to - p_from > interval '7305 days' then
        perform glebosaurus.fail('22023', 'okno wykresu: od < do, najwyżej 20 lat');
    end if;
    v_zone := coalesce(nullif(p_zone, ''), (select time_zone from glebosaurus.settings));
    if not glebosaurus.known_time_zone(v_zone) then v_zone := (select time_zone from glebosaurus.settings); end if;
    return glebosaurus.series(p_sensor, p_from, p_to, p_points, v_zone,
        coalesce(v_sensor.read_interval_minutes, (select read_interval_minutes from glebosaurus.settings)));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Strona czujnika
-- ---------------------------------------------------------------------------------------------------------------------

-- Wszystko o jednym czujniku poza wykresem: ustawienia, ostatni odczyt, dzierżawa, kto go słyszy (bez historii),
-- otwarte alerty, najstarszy i najnowszy odczyt. Produkt Tuya z danych przypisania – bez kluczy.
create function public.owner_sensor(p_id text) returns jsonb
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
            'id', v_sensor.id, 'name', v_sensor.name, 'mac', v_sensor.mac, 'enabled', v_sensor.enabled,
            'archivedAt', v_sensor.archived_at,
            'readIntervalMinutes', coalesce(v_sensor.read_interval_minutes, (select read_interval_minutes from glebosaurus.settings)),
            'ownReadInterval', v_sensor.read_interval_minutes is not null,
            'wateringBelowPercent', v_sensor.watering_below_percent,
            'wateringClearAbovePercent', v_sensor.watering_clear_above_percent,
            'consecutiveReadings', v_sensor.consecutive_readings,
            'lowBatteryPercent', v_sensor.low_battery_percent,
            'silentAfterHours', v_sensor.silent_after_hours,
            'productId', glebosaurus.secret_get('glebosaurus:sensor:' || v_sensor.id)::jsonb->>'productId'),
        'last', (select jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                          'battery', r.battery, 'batteryState', r.battery_state, 'agent', a.name)
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

-- Ostatnie odczyty czujnika (najwyżej 1000), od najnowszego.
create function public.owner_readings(p_id text, p_count int) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    return coalesce((
        select jsonb_agg(jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                            'battery', r.battery, 'batteryState', r.battery_state, 'agent', r.agent) order by r.at desc)
          from (select x.at, x.moisture, x.temp_tenths, x.battery, x.battery_state, a.name as agent
                  from glebosaurus.readings x left join glebosaurus.agents a on a.id = x.agent_id
                 where x.sensor_id = p_id order by x.at desc limit least(greatest(coalesce(p_count, 96), 1), 1000)) r), '[]'::jsonb);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Pulpit z otwartymi alertami i odbiorcami raportu na żądanie
-- (jak w 20261121140000_owner_config.sql; zmiany: alerts, reportRecipients, odstęp odczytu czujnika)
-- ---------------------------------------------------------------------------------------------------------------------

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
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', coalesce(s.read_interval_minutes, v_settings.read_interval_minutes),
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'lowBatteryPercent', s.low_battery_percent,
                'last', (select jsonb_build_object('at', r.at, 'moisture', r.moisture, 'temperature', round(r.temp_tenths / 10.0, 1),
                                                   'battery', r.battery, 'batteryState', r.battery_state, 'agent', ra.name)
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
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.owner_series(text, timestamptz, timestamptz, int, text) from public, anon, authenticated, service_role;
revoke all on function public.owner_sensor(text) from public, anon, authenticated, service_role;
revoke all on function public.owner_readings(text, int) from public, anon, authenticated, service_role;

grant execute on function public.owner_series(text, timestamptz, timestamptz, int, text) to authenticated;
grant execute on function public.owner_sensor(text) to authenticated;
grant execute on function public.owner_readings(text, int) to authenticated;

comment on function public.owner_series(text, timestamptz, timestamptz, int, text) is 'glebosaurus-api';
comment on function public.owner_sensor(text) is 'glebosaurus-api';
comment on function public.owner_readings(text, int) is 'glebosaurus-api';
