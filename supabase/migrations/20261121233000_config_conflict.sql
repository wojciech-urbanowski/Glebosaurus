-- Faza 3, poprawka: konflikt wersji konfiguracji kodem PT409 zamiast 40001.
--
-- Data API (PostgREST) ponawia transakcję po każdym SQLSTATE 40001 (serialization_failure), także zgłoszonym przez
-- funkcję: zapis ze strony z nieaktualną wersją kręcił się ok. 50 s (ok. 1000 ponowień na sekundę, dalej po zerwaniu
-- połączenia przez klienta) i nie wracał z „conflict” (znalezione 2026-11-21 testem DataApiOwnerTests przez prawdziwe
-- Data API; połączenie bezpośrednie, którym idą pozostałe testy SQL, nie ponawia). Kod PTxyz PostgREST oddaje jako
-- status HTTP xyz – tu 409 Conflict, raz. Strona rozpoznaje konflikt po przedrostku „Glebosaurus: conflict”, więc treść
-- zostaje bez zmian. Żadna funkcja nie zgłasza już kodów z klasy 40 – pilnuje tego test statyczny SqlDataApiRulesTests.

create or replace function glebosaurus.require_config_version(p_version bigint) returns void
language plpgsql set search_path = '' as $$
declare
    v_current bigint;
begin
    select config_version into v_current from glebosaurus.settings for update;
    if p_version is distinct from v_current then
        perform glebosaurus.fail('PT409', 'conflict: konfigurację zmieniono w międzyczasie (teraz wersja ' || v_current ||
                                          ') – odśwież stronę i wprowadź zmianę jeszcze raz');
    end if;
end
$$;

revoke all on function glebosaurus.require_config_version(bigint) from public, anon, authenticated, service_role;
