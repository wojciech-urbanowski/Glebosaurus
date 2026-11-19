-- Glebosaurus, faza 2.2: stan urządzeń agentów w panelu WWW (docs/ARCHITEKTURA.md, zasady 3, 11 i 12). Agent i tak
-- wysyła w każdym agent_sync sygnał życia (wersja, platforma, Bluetooth; telefon także bateria, temperatura, ładowarka,
-- sieć i stan cieplny) – baza trzyma go w agents.last_status, a lista agentów w odpowiedzi agent_sync oddaje go teraz
-- innym agentom. Telefon niczego nie wystawia sam: panel na innym agencie widzi jego stan z ostatniej synchronizacji.

-- Liczba z JSON w zakresie albo null (zły typ, poza zakresem). Zagnieżdżony CASE – rzutowanie dopiero po sprawdzeniu typu.
create function glebosaurus.json_number(p_value jsonb, p_min numeric, p_max numeric) returns numeric
language sql immutable set search_path = '' as $$
    select case when jsonb_typeof(p_value) = 'number' then
        case when (p_value #>> '{}')::numeric between p_min and p_max then (p_value #>> '{}')::numeric end
    end
$$;

-- Sygnał życia agenta w postaci, którą czytają inni agenci: tylko znane pola o właściwym typie, w zakresie i o
-- ograniczonej długości. Status zapisuje agent dowolnym obiektem, a czytają go wszyscy – pole złego typu (np. starszy
-- albo błędny agent) nie może zepsuć innym synchronizacji. Bez wersji i platformy – null.
create function glebosaurus.agent_status_view(p_status jsonb) returns jsonb
language sql immutable set search_path = '' as $$
    select case
        when jsonb_typeof(p_status) = 'object' and jsonb_typeof(p_status->'version') = 'string'
             and jsonb_typeof(p_status->'platform') = 'string' then
            jsonb_strip_nulls(jsonb_build_object(
                'version', left(p_status->>'version', 40),
                'platform', left(p_status->>'platform', 20),
                'ble', case when jsonb_typeof(p_status->'ble') = 'string' then left(p_status->>'ble', 100) end,
                'batteryPercent', round(glebosaurus.json_number(p_status->'batteryPercent', 0, 100))::int,
                'batteryTemperatureCelsius', round(glebosaurus.json_number(p_status->'batteryTemperatureCelsius', -50, 150), 1),
                'charging', case when jsonb_typeof(p_status->'charging') = 'boolean' then (p_status->>'charging')::boolean end,
                'network', case when jsonb_typeof(p_status->'network') = 'string'
                                     and p_status->>'network' in ('wifi', 'cellular', 'ethernet', 'other')
                                then p_status->>'network' end,
                'wifiDbm', round(glebosaurus.json_number(p_status->'wifiDbm', -126, 0))::int,
                'thermalStatus', case when jsonb_typeof(p_status->'thermalStatus') = 'string'
                                           and p_status->>'thermalStatus' in ('none', 'light', 'moderate', 'severe',
                                                                              'critical', 'emergency', 'shutdown')
                                      then p_status->>'thermalStatus' end))
    end
$$;

-- agent_sync jak w 20261116220000_alerts_commands_logs.sql; jedyna zmiana: lista agentów ma ich sygnał życia („status”).
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
                                                'lastSeenSecondsAgo', glebosaurus.seconds(v_now - a.last_seen_at),
                                                'status', glebosaurus.agent_status_view(a.last_status))
                             order by a.name)
              from glebosaurus.agents a where a.enabled and a.token_hash is not null), '[]'::jsonb));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia (public.agent_sync zachowuje swoje przy create or replace)
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;
