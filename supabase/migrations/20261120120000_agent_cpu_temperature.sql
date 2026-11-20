-- Glebosaurus: temperatura procesora agenta linuksowego (Raspberry Pi) w sygnale życia (docs/ARCHITEKTURA.md, zasady 3,
-- 12 i 13). Agent na Linuksie wysyła ją w każdym agent_sync, a nadawca pokazuje ją w raporcie (sekcja „Urządzenia”) obok
-- baterii i temperatury telefonu. Lista agentów w agent_sync oddaje tylko pola przepuszczone przez agent_status_view,
-- więc nowe pole wymaga tej migracji; agent_sync się nie zmienia.

-- agent_status_view jak w 20261119120000_agent_device_status.sql; jedyna zmiana: pole „cpuTemperatureCelsius”.
create or replace function glebosaurus.agent_status_view(p_status jsonb) returns jsonb
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
                'cpuTemperatureCelsius', round(glebosaurus.json_number(p_status->'cpuTemperatureCelsius', -50, 150), 1),
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

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia (funkcja zachowuje swoje przy create or replace; jak w poprzednich migracjach – na wszelki wypadek)
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;
