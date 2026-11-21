-- Glebosaurus, faza 3 (etap 5, 3d) – parowanie zdalne z przeglądarki (docs/ARCHITEKTURA.md, zasady 5, 10 i 13):
-- właściciel zleca „pair” wybranemu agentowi w zasięgu, agent zgłasza postęp (skan ruszył – dopiero wtedy strona mówi
-- „włóż baterie”, znaleziony, przypisanie, weryfikacja), klucz trafia do Vault PRZED wysłaniem do czujnika (przerwane
-- parowanie wznawia się tym samym kluczem), a po udanej weryfikacji baza sama zakłada czujnik (albo – po resecie
-- przyciskiem – podmienia klucz istniejącego).
--
-- Zgodność: nowe kolumny i funkcje; starszy agent nie zna „pair” i zostawia polecenie, aż wygaśnie (15 min).

alter table glebosaurus.commands add column progress jsonb check (progress is null or (jsonb_typeof(progress) = 'object' and length(progress::text) <= 2000));
alter table glebosaurus.commands add column progress_at timestamptz;

-- Polecenie parowania czekające na tego agenta (tylko jego, tylko niewykonane i nieprzeterminowane).
create function glebosaurus.pair_command(p_agent glebosaurus.agents, p_id bigint) returns glebosaurus.commands
language plpgsql set search_path = '' as $$
declare
    v_command glebosaurus.commands;
begin
    select * into v_command from glebosaurus.commands
     where id = p_id and kind = 'pair' and target_agent_id = p_agent.id and done_at is null and expires_at > glebosaurus.clock()
     for update;
    if not found then perform glebosaurus.fail('22023', 'nie ma czekającego parowania ' || coalesce(p_id::text, '∅') || ' dla tego agenta'); end if;
    if coalesce(v_command.params->>'sensor', '') !~ '^[a-z0-9][a-z0-9_-]{0,39}$' then
        perform glebosaurus.fail('22023', 'parowanie bez poprawnego id czujnika');
    end if;
    return v_command;
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API agentów
-- ---------------------------------------------------------------------------------------------------------------------

-- Postęp długiego polecenia (parowanie) – strona pokazuje go co kilka sekund.
create function public.command_progress(p_token text, p_id bigint, p_progress jsonb) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents := glebosaurus.agent_auth(p_token);
begin
    if p_progress is null or jsonb_typeof(p_progress) <> 'object' or length(p_progress::text) > 2000 then
        perform glebosaurus.fail('22023', 'postęp: obiekt JSON do 2000 znaków');
    end if;
    update glebosaurus.commands set progress = p_progress, progress_at = glebosaurus.clock()
     where id = p_id and done_at is null and expires_at > glebosaurus.clock()
       and (target_agent_id = v_agent.id or taken_by = v_agent.id);
    return found;
end
$$;

-- Klucz do wznowienia: zapisany wcześniej klucz tego parowania (przerwane parowanie) albo klucz istniejącego czujnika
-- o tym id (po resecie przyciskiem przypisujemy ten sam klucz); null – nowy czujnik, agent wymyśli klucz.
create function public.pair_key_get(p_token text, p_id bigint) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent   glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_command glebosaurus.commands := glebosaurus.pair_command(v_agent, p_id);
    v_sensor  text := v_command.params->>'sensor';
begin
    return jsonb_build_object('credentials', coalesce(glebosaurus.secret_get('glebosaurus:pairing:' || v_sensor)::jsonb,
                                                      glebosaurus.secret_get('glebosaurus:sensor:' || v_sensor)::jsonb));
end
$$;

-- Klucz parowania do Vault – agent woła to, ZANIM wyśle klucz do czujnika.
create function public.pair_key_put(p_token text, p_id bigint, p_credentials jsonb) returns boolean
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent       glebosaurus.agents := glebosaurus.agent_auth(p_token);
    v_command     glebosaurus.commands := glebosaurus.pair_command(v_agent, p_id);
    v_sensor      text := v_command.params->>'sensor';
    v_credentials jsonb := glebosaurus.normalize_credentials(p_credentials);
begin
    perform glebosaurus.require_unique_device(v_sensor, v_credentials);
    perform glebosaurus.secret_put('glebosaurus:pairing:' || v_sensor, v_credentials::text);
    return true;
end
$$;

-- Udane parowanie (klucz sprawdzony odczytem): czujnik powstaje (albo dostaje nowy klucz i MAC), klucz parowania staje się
-- kluczem czujnika, polecenie jest wykonane z wynikiem. Agenci dostaną czujnik przy najbliższej synchronizacji.
create function public.pair_complete(p_token text, p_id bigint, p_result jsonb) returns jsonb
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
    update glebosaurus.settings set updated_at = now();
    update glebosaurus.commands set done_at = glebosaurus.clock(),
           result = (case when jsonb_typeof(p_result) = 'object' and length(p_result::text) <= 3000 then p_result else '{}'::jsonb end)
                    || jsonb_build_object('ok', true, 'created', v_created, 'mac', v_credentials->>'mac')
     where id = p_id;
    return jsonb_build_object('sensor', v_sensor, 'created', v_created, 'mac', v_credentials->>'mac');
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- API strony
-- ---------------------------------------------------------------------------------------------------------------------

-- Parowanie przez agenta p_agent: nowy czujnik p_sensor (albo nowy klucz istniejącego – po resecie przyciskiem); p_mac
-- zawęża wybór, gdy w zasięgu jest kilka niesparowanych czujników. Jedno parowanie naraz na agenta (powtórzone zlecenie
-- zwraca czekające).
create function public.owner_pair_start(p_agent text, p_sensor text, p_name text, p_mac text) returns jsonb
language plpgsql security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_agent glebosaurus.agents;
    v_mac   text := nullif(upper(trim(coalesce(p_mac, ''))), '');
begin
    perform glebosaurus.require_owner();
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
    if v_mac is not null and v_mac !~ '^[0-9A-F]{2}([:-]?[0-9A-F]{2}){5}$' then
        perform glebosaurus.fail('22023', 'MAC: np. DC:23:51:CB:F7:7D');
    end if;
    perform glebosaurus.sensor_name(p_name, p_sensor);
    return glebosaurus.command_create('pair', null, v_agent.id,
        jsonb_strip_nulls(jsonb_build_object('sensor', p_sensor, 'name', nullif(trim(coalesce(p_name, '')), ''), 'mac', v_mac)),
        'owner-web');
end
$$;

-- Jak w 20261121140000_owner_config.sql; zmiana: postęp polecenia (parowanie).
create or replace function public.owner_command_status(p_ids bigint[]) returns jsonb
language plpgsql stable security definer set search_path = '' set timezone = 'UTC' as $$
declare
    v_now timestamptz;
begin
    perform glebosaurus.require_owner();
    v_now := glebosaurus.clock();
    return coalesce((
        select jsonb_agg(jsonb_build_object(
            'id', c.id, 'kind', c.kind, 'sensor', coalesce(c.sensor_id, c.params->>'sensor'), 'createdAt', c.created_at,
            'expiresAt', c.expires_at, 'takenBy', a.name, 'takenAt', c.taken_at, 'doneAt', c.done_at, 'result', c.result,
            'progress', c.progress, 'progressAt', c.progress_at,
            'expired', c.done_at is null and c.expires_at <= v_now) order by c.id)
          from glebosaurus.commands c left join glebosaurus.agents a on a.id = c.taken_by
         where c.id = any(p_ids[1:50])), '[]'::jsonb);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.command_progress(text, bigint, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.pair_key_get(text, bigint) from public, anon, authenticated, service_role;
revoke all on function public.pair_key_put(text, bigint, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.pair_complete(text, bigint, jsonb) from public, anon, authenticated, service_role;
revoke all on function public.owner_pair_start(text, text, text, text) from public, anon, authenticated, service_role;

grant execute on function public.command_progress(text, bigint, jsonb) to anon;
grant execute on function public.pair_key_get(text, bigint) to anon;
grant execute on function public.pair_key_put(text, bigint, jsonb) to anon;
grant execute on function public.pair_complete(text, bigint, jsonb) to anon;
grant execute on function public.owner_pair_start(text, text, text, text) to authenticated;

comment on function public.command_progress(text, bigint, jsonb) is 'glebosaurus-api';
comment on function public.pair_key_get(text, bigint) is 'glebosaurus-api';
comment on function public.pair_key_put(text, bigint, jsonb) is 'glebosaurus-api';
comment on function public.pair_complete(text, bigint, jsonb) is 'glebosaurus-api';
comment on function public.owner_pair_start(text, text, text, text) is 'glebosaurus-api';
