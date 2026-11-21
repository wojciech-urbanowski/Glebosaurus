-- Glebosaurus, faza 3 (etap 4, 3b) – alerty i historia maili na stronie właściciela (docs/ARCHITEKTURA.md, zasady 9,
-- 10 i 13): potwierdzenie alertu (koniec przypomnień), wyciszenie alertów czujnika do chwili, wyciszenie wszystkich
-- (urlop), ręczne zamknięcie (gdy stan trwa, nadawca otworzy nowy epizod) i archiwum wysłanych raportów i alertów
-- (HTML spakowany gzipem, 90 dni) zapisywane przez nadawcę po wysyłce.
--
-- Zgodność: tylko nowe kolumny, pola i funkcje. Starszy nadawca nie zna potwierdzeń ani wyciszeń (przypomina dalej) i
-- nie zapisuje archiwum – strona pokazuje wtedy pustą historię.

-- ---------------------------------------------------------------------------------------------------------------------
-- Schemat
-- ---------------------------------------------------------------------------------------------------------------------

alter table glebosaurus.alerts add column acknowledged_at timestamptz;
alter table glebosaurus.alerts add column closed_manually boolean not null default false;
alter table glebosaurus.sensors add column alerts_muted_until timestamptz;
alter table glebosaurus.settings add column alerts_muted_until timestamptz;
create index alerts_resolved on glebosaurus.alerts (resolved_at) where resolved_at is not null;

-- Wysłane maile do podglądu na stronie (bez logów administratora – te mają duże załączniki). Klucz = pierwszy klucz
-- kolejki maila; mail o kilku alertach ma ich kilka (keys).
create table glebosaurus.mail_archive (
    key        text primary key check (length(key) <= 200),
    keys       text[] not null,
    kind       text not null check (kind in ('report', 'report-request', 'alert', 'mail-test', 'agent-silent')),
    subject    text not null check (length(subject) between 1 and 300),
    recipients text[] not null,
    agent_id   int references glebosaurus.agents on delete set null,
    sent_at    timestamptz not null,
    html_gzip  bytea not null,
    size_bytes int not null
);
create index mail_archive_sent on glebosaurus.mail_archive (sent_at);
alter table glebosaurus.mail_archive enable row level security;

-- ---------------------------------------------------------------------------------------------------------------------
-- Agenci: potwierdzenia i wyciszenia w alertach i konfiguracji
-- ---------------------------------------------------------------------------------------------------------------------

-- Jak w 20261116220000_alerts_commands_logs.sql; zmiana: acknowledgedAt, closedManually.
create or replace function glebosaurus.alert_json(p_alert glebosaurus.alerts) returns jsonb
language sql stable set search_path = '' as $$
    select jsonb_build_object('id', p_alert.id, 'sensor', p_alert.sensor_id, 'kind', p_alert.kind,
        'detail', p_alert.detail, 'openedAt', p_alert.opened_at, 'lastNotifiedAt', p_alert.last_notified_at,
        'resolvedAt', p_alert.resolved_at, 'acknowledgedAt', p_alert.acknowledged_at,
        'closedManually', p_alert.closed_manually)
$$;

-- Jak w 20261121140000_owner_config.sql; zmiana: alertsMutedUntil w ustawieniach i przy czujnikach.
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
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', s.read_interval_minutes,
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'consecutiveReadings', s.consecutive_readings,
                'lowBatteryPercent', s.low_battery_percent,
                'silentAfterHours', s.silent_after_hours,
                'alertsMutedUntil', s.alerts_muted_until,
                'tuya', glebosaurus.secret_get('glebosaurus:sensor:' || s.id)::jsonb) order by s.id)
            from glebosaurus.sensors s where s.archived_at is null), '[]'::jsonb));
end
$$;

-- Archiwum wysłanego maila – tylko obecny nadawca i tylko mail, który sam zarezerwował w kolejce. HTML spakowany gzipem
-- (base64, najwyżej 1 MB po spakowaniu). Zwraca false, gdy mail już jest w archiwum. Przy okazji kasuje starsze niż 90 dni.
create function public.mail_archive_put(p_token text, p_epoch bigint, p_keys text[], p_subject text, p_recipients text[],
    p_html_gzip text) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_now   timestamptz := glebosaurus.clock();
    v_html  bytea;
    v_kind  text;
begin
    perform glebosaurus.require_notifier(v_agent, p_epoch);
    if p_keys is null or cardinality(p_keys) not between 1 and 50 then
        perform glebosaurus.fail('22023', 'archiwum: podaj klucze maila (1–50)');
    end if;
    v_kind := split_part(p_keys[1], '|', 1);
    if v_kind not in ('report', 'report-request', 'alert', 'mail-test', 'agent-silent') then
        perform glebosaurus.fail('22023', 'archiwum: ten rodzaj maila nie trafia do archiwum');
    end if;
    if not exists (select 1 from glebosaurus.mail_outbox where key = p_keys[1] and agent_id = v_agent.id) then
        perform glebosaurus.fail('22023', 'archiwum: mail nie był zarezerwowany przez tego agenta');
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
            coalesce(p_recipients[1:50], '{}'), v_agent.id, v_now, v_html, length(v_html))
    on conflict (key) do nothing;
    if not found then return false; end if;
    delete from glebosaurus.mail_archive where sent_at < v_now - interval '90 days';
    return true;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: alerty
-- ---------------------------------------------------------------------------------------------------------------------

-- Otwarte alerty i zamknięte z ostatnich p_days dni (najwyżej 90) z nazwami czujników, wyciszenia.
create function public.owner_alerts(p_days int) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    return jsonb_build_object(
        'serverTime', v_now,
        'alertReminderHours', (select alert_reminder_hours from glebosaurus.settings),
        'alertsMutedUntil', (select case when alerts_muted_until > v_now then alerts_muted_until end from glebosaurus.settings),
        'open', coalesce((
            select jsonb_agg(glebosaurus.alert_json(a) || jsonb_build_object('sensorName', s.name) order by a.opened_at)
              from glebosaurus.alerts a join glebosaurus.sensors s on s.id = a.sensor_id
             where a.resolved_at is null), '[]'::jsonb),
        'closed', coalesce((
            select jsonb_agg(glebosaurus.alert_json(a) || jsonb_build_object('sensorName', s.name) order by a.resolved_at desc)
              from (select * from glebosaurus.alerts
                     where resolved_at > v_now - make_interval(days => least(greatest(coalesce(p_days, 30), 1), 90))
                     order by resolved_at desc limit 200) a
              join glebosaurus.sensors s on s.id = a.sensor_id), '[]'::jsonb),
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object('id', s.id, 'name', s.name,
                                                'alertsMutedUntil', case when s.alerts_muted_until > v_now then s.alerts_muted_until end)
                             order by s.name, s.id)
              from glebosaurus.sensors s where s.archived_at is null), '[]'::jsonb));
end
$$;

-- Potwierdzenie (true) albo jego cofnięcie (false): potwierdzony alert nie dostaje przypomnień, zamyka się sam.
create function public.owner_alert_acknowledge(p_id bigint, p_acknowledge boolean) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_alert glebosaurus.alerts;
begin
    perform glebosaurus.require_owner();
    update glebosaurus.alerts set acknowledged_at = case when p_acknowledge then coalesce(acknowledged_at, glebosaurus.clock()) end
     where id = p_id and resolved_at is null
    returning * into v_alert;
    if not found then perform glebosaurus.fail('22023', 'nie ma otwartego alertu ' || coalesce(p_id::text, '∅') || ' (mógł się już zamknąć)'); end if;
    return glebosaurus.alert_json(v_alert);
end
$$;

-- Ręczne zamknięcie: alert znika z otwartych; jeśli stan trwa, nadawca otworzy nowy epizod (z nowym mailem).
create function public.owner_alert_close(p_id bigint) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_alert glebosaurus.alerts;
begin
    perform glebosaurus.require_owner();
    update glebosaurus.alerts set resolved_at = glebosaurus.clock(), closed_manually = true
     where id = p_id and resolved_at is null
    returning * into v_alert;
    if not found then perform glebosaurus.fail('22023', 'nie ma otwartego alertu ' || coalesce(p_id::text, '∅') || ' (mógł się już zamknąć)'); end if;
    return glebosaurus.alert_json(v_alert);
end
$$;

-- Wyciszenie alertów mailowych: czujnika (p_sensor) albo wszystkich (p_sensor null – urlop) do p_until (najwyżej 90 dni
-- naprzód); p_until null – koniec wyciszenia. Alerty dalej się otwierają i zamykają, tylko maile czekają; po wyciszeniu
-- trwający alert dostaje mail. Zmiana podnosi wersję konfiguracji (agenci dostają ją w get_config).
create function public.owner_alerts_mute(p_sensor text, p_until timestamptz) returns jsonb
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
        update glebosaurus.settings set alerts_muted_until = p_until;
    else
        update glebosaurus.sensors set alerts_muted_until = p_until, updated_at = now() where id = p_sensor;
        if not found then perform glebosaurus.fail('22023', 'nie ma czujnika ' || p_sensor); end if;
    end if;
    return jsonb_build_object('sensor', p_sensor, 'alertsMutedUntil', p_until,
                              'configVersion', (select config_version from glebosaurus.settings));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony: historia maili
-- ---------------------------------------------------------------------------------------------------------------------

create function public.owner_mail_history(p_days int) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
begin
    perform glebosaurus.require_owner();
    return coalesce((
        select jsonb_agg(jsonb_build_object('key', m.key, 'kind', m.kind, 'subject', m.subject, 'recipients', to_jsonb(m.recipients),
                                            'sentAt', m.sent_at, 'agent', a.name, 'sizeBytes', m.size_bytes,
                                            'alerts', cardinality(m.keys)) order by m.sent_at desc)
          from (select * from glebosaurus.mail_archive
                 where sent_at > glebosaurus.clock() - make_interval(days => least(greatest(coalesce(p_days, 30), 1), 90))
                 order by sent_at desc limit 500) m
          left join glebosaurus.agents a on a.id = m.agent_id), '[]'::jsonb);
end
$$;

-- Treść jednego maila (HTML spakowany gzipem, base64) – strona rozpakowuje ją i pokazuje w izolowanej ramce.
create function public.owner_mail_body(p_key text) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_mail glebosaurus.mail_archive;
begin
    perform glebosaurus.require_owner();
    select * into v_mail from glebosaurus.mail_archive where key = p_key;
    if not found then perform glebosaurus.fail('22023', 'nie ma takiego maila w archiwum (starsze niż 90 dni są usuwane)'); end if;
    return jsonb_build_object('key', v_mail.key, 'subject', v_mail.subject, 'sentAt', v_mail.sent_at,
                              'recipients', to_jsonb(v_mail.recipients), 'htmlGzip', encode(v_mail.html_gzip, 'base64'));
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Pulpit z wyciszeniami (jak w 20261121160000_owner_history.sql; zmiany: alertsMutedUntil ogólne i przy czujnikach)
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
        'alertsMutedUntil', case when v_settings.alerts_muted_until > v_now then v_settings.alerts_muted_until end,
        'sensors', coalesce((
            select jsonb_agg(jsonb_build_object(
                'id', s.id, 'name', s.name, 'mac', s.mac, 'enabled', s.enabled,
                'readIntervalMinutes', coalesce(s.read_interval_minutes, v_settings.read_interval_minutes),
                'wateringBelowPercent', s.watering_below_percent,
                'wateringClearAbovePercent', s.watering_clear_above_percent,
                'lowBatteryPercent', s.low_battery_percent,
                'alertsMutedUntil', case when s.alerts_muted_until > v_now then s.alerts_muted_until end,
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

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.mail_archive_put(text, bigint, text[], text, text[], text) from public, anon, authenticated, service_role;
revoke all on function public.owner_alerts(int) from public, anon, authenticated, service_role;
revoke all on function public.owner_alert_acknowledge(bigint, boolean) from public, anon, authenticated, service_role;
revoke all on function public.owner_alert_close(bigint) from public, anon, authenticated, service_role;
revoke all on function public.owner_alerts_mute(text, timestamptz) from public, anon, authenticated, service_role;
revoke all on function public.owner_mail_history(int) from public, anon, authenticated, service_role;
revoke all on function public.owner_mail_body(text) from public, anon, authenticated, service_role;

grant execute on function public.mail_archive_put(text, bigint, text[], text, text[], text) to anon;
grant execute on function public.owner_alerts(int) to authenticated;
grant execute on function public.owner_alert_acknowledge(bigint, boolean) to authenticated;
grant execute on function public.owner_alert_close(bigint) to authenticated;
grant execute on function public.owner_alerts_mute(text, timestamptz) to authenticated;
grant execute on function public.owner_mail_history(int) to authenticated;
grant execute on function public.owner_mail_body(text) to authenticated;

comment on function public.mail_archive_put(text, bigint, text[], text, text[], text) is 'glebosaurus-api';
comment on function public.owner_alerts(int) is 'glebosaurus-api';
comment on function public.owner_alert_acknowledge(bigint, boolean) is 'glebosaurus-api';
comment on function public.owner_alert_close(bigint) is 'glebosaurus-api';
comment on function public.owner_alerts_mute(text, timestamptz) is 'glebosaurus-api';
comment on function public.owner_mail_history(int) is 'glebosaurus-api';
comment on function public.owner_mail_body(text) is 'glebosaurus-api';
