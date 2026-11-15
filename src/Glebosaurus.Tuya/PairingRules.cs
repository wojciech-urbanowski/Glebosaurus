namespace Glebosaurus.Tuya;

/// <summary>
/// Reguła wyboru urządzenia do sparowania. Wokół jest wiele innych urządzeń Tuya (część to czujniki użytkownika wciąż
/// sparowane ze Smart Life, część może być cudza), więc przypisanie nigdy nie może trafić w
/// przypadkowe: bierzemy tylko niesparowane, łączliwe, protokołu 3 i znanego typu produktu (albo – przy wznawianiu
/// przerwanego parowania – urządzenie o UUID z pliku klucza), a przy podanym adresie tylko ten adres.
/// </summary>
public static class PairingRules
{
    /// <param name="identity">Tożsamość z pełnej reklamy; null, gdy reklama jest jeszcze niepełna.</param>
    /// <param name="address">Adres urządzenia, jeśli platforma go ujawnia.</param>
    /// <param name="existing">Plik klucza z przerwanego parowania (wznawianie) albo null przy nowym czujniku.</param>
    /// <param name="wantedMac">Adres wskazany przez użytkownika (--mac) albo null.</param>
    public static bool IsCandidate(TuyaIdentity? identity, bool? isConnectable, ulong? address, TuyaCredentials? existing, ulong? wantedMac)
    {
        if (isConnectable == false || identity is not { ProtocolMajor: 3 }) return false;
        if (wantedMac is { } wanted && address != wanted) return false;
        if (existing is not null) return identity.Uuid == existing.Uuid;
        return !identity.IsBound && SoilSensorProfiles.ForProduct(identity.ProductId) is not null;
    }
}
