-- Glebosaurus, faza 2.1.4: kopia odczytów wszystkich agentów u agenta z lokalnym panelem WWW (docs/ARCHITEKTURA.md,
-- zasada 15). Agent dociąga z chmury to, czego jeszcze nie ma, po numerze kolejnym wiersza – także odczyty dosłane
-- z opóźnieniem (agent bez sieci, zaległe pomiary z pamięci czujnika), których nie złapałoby okno po czasie pomiaru.

-- Numer kolejny i chwila przyjęcia odczytu. Istniejące wiersze dostają numery w dowolnej kolejności i chwilę migracji.
alter table glebosaurus.readings add column seq bigint generated always as identity;
alter table glebosaurus.readings add column received_at timestamptz not null default glebosaurus.clock();
create unique index readings_seq on glebosaurus.readings (seq);
create index readings_received_at on glebosaurus.readings (received_at);

-- Odczyty przyjęte po numerze p_after (od najstarszego numeru), najwyżej p_limit; „last” to numer, od którego pytać
-- następnym razem, „more” – czy czeka więcej.
--
-- Numery nadaje sekwencja przy wstawianiu, a widać je dopiero po zatwierdzeniu transakcji – wiersz o niższym numerze może
-- się pojawić później niż wiersz o wyższym. Żeby żaden nie przepadł, funkcja zwraca tylko wiersze „ustalone”: przyjęte
-- co najmniej 30 s temu i o numerze niższym niż każdy widoczny wiersz nieustalony (bariera). Transakcje wstawiające
-- odczyty to pojedyncze wywołania Data API (agent_sync), ograniczone limitem czasu instrukcji ról API (kilka sekund),
-- więc wiersz o niższym numerze, którego jeszcze nie widać, należy do transakcji, która zaczęła się niedawno – a wtedy
-- każdy wiersz o wyższym numerze jest nieustalony i zatrzymuje się na barierze.
create function public.readings_feed(p_token text, p_after bigint, p_limit int default 5000) returns jsonb
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
                                                 'batteryState', r.battery_state) order by r.seq), '[]'::jsonb),
           max(r.seq), count(*)
      into v_readings, v_last, v_count
      from (select * from glebosaurus.readings
             where seq > p_after and (v_barrier is null or seq < v_barrier)
             order by seq limit p_limit) r;

    return jsonb_build_object('readings', v_readings, 'last', coalesce(v_last, p_after), 'more', v_count = p_limit);
end
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Uprawnienia
-- ---------------------------------------------------------------------------------------------------------------------

revoke all on all tables in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all sequences in schema glebosaurus from public, anon, authenticated, service_role;
revoke all on all functions in schema glebosaurus from public, anon, authenticated, service_role;

revoke all on function public.readings_feed(text, bigint, int) from public, anon, authenticated, service_role;
grant execute on function public.readings_feed(text, bigint, int) to anon;
comment on function public.readings_feed(text, bigint, int) is 'glebosaurus-api';
