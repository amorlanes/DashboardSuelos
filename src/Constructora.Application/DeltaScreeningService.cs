using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructora.Application;

public sealed record ElementoBim(string GlobalId, string Familia, string Tipo,
    double Volumen, double Area, string CodigoPartida);

public enum UnidadMedicionBim { MetroCubico, MetroCuadrado, Unidad }

public sealed record TarifaPartidaBim(string CodigoPartida, UnidadMedicionBim Unidad,
    decimal PrecioUnitario);

/// <summary>Una diferencia es significativa si supera tanto la tolerancia absoluta como la relativa.</summary>
public sealed record ToleranciaGeometricaBim(double Absoluta = 1e-6, double Relativa = 1e-6);

public sealed record ElementoBimModificado(ElementoBim Anterior, ElementoBim Nuevo);

public sealed record ResultadoDelta(IReadOnlyList<ElementoBim> ElementosAnadidos,
    IReadOnlyList<ElementoBim> ElementosEliminados,
    IReadOnlyList<ElementoBimModificado> ElementosModificados,
    decimal ImpactoEconomicoEstimado);

/// <summary>
/// Compara modelos en O(anterior + nuevo + tarifas), con claves sensibles a mayúsculas.
/// El impacto económico suma altas, bajas y la diferencia de cada elemento modificado.
/// Las variaciones geométricas dentro de las tolerancias se ignoran también al valorar el delta.
/// Familia y Tipo se conservan como metadatos y no determinan un cambio de medición.
/// </summary>
public sealed class DeltaScreeningService
{
    private readonly ILogger<DeltaScreeningService> _logger;
    private readonly ToleranciaGeometricaBim _tolerancia;

    public DeltaScreeningService(ILogger<DeltaScreeningService>? logger = null,
        ToleranciaGeometricaBim? tolerancia = null)
    {
        _logger = logger ?? NullLogger<DeltaScreeningService>.Instance;
        _tolerancia = tolerancia ?? new ToleranciaGeometricaBim();
        if (!double.IsFinite(_tolerancia.Absoluta) || _tolerancia.Absoluta < 0 ||
            !double.IsFinite(_tolerancia.Relativa) || _tolerancia.Relativa < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancia),
                "Las tolerancias deben ser finitas y no negativas.");
    }

    public ResultadoDelta Comparar(IEnumerable<ElementoBim> versionAnterior,
        IEnumerable<ElementoBim> versionNueva, IEnumerable<TarifaPartidaBim> tarifas,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(versionAnterior);
            ArgumentNullException.ThrowIfNull(versionNueva);
            ArgumentNullException.ThrowIfNull(tarifas);

            var precios = IndexarTarifas(tarifas, cancellationToken);
            var anteriores = IndexarElementos(versionAnterior, nameof(versionAnterior), precios,
                cancellationToken);
            var nuevos = IndexarElementos(versionNueva, nameof(versionNueva), precios,
                cancellationToken);
            var anadidos = new List<ElementoBim>();
            var eliminados = new List<ElementoBim>();
            var modificados = new List<ElementoBimModificado>();
            decimal impacto = 0;

            foreach (var actual in nuevos.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!anteriores.TryGetValue(actual.GlobalId, out var anterior))
                {
                    anadidos.Add(actual);
                    impacto = checked(impacto + Valorar(actual, precios));
                }
                else if (HaCambiado(anterior, actual))
                {
                    modificados.Add(new ElementoBimModificado(anterior, actual));
                    impacto = checked(impacto + Valorar(actual, precios) - Valorar(anterior, precios));
                }
            }

            foreach (var anterior in anteriores.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nuevos.ContainsKey(anterior.GlobalId))
                {
                    eliminados.Add(anterior);
                    impacto = checked(impacto - Valorar(anterior, precios));
                }
            }

            _logger.LogInformation("Delta BIM: {Anadidos} altas, {Eliminados} bajas, " +
                "{Modificados} modificaciones; impacto {Impacto}", anadidos.Count,
                eliminados.Count, modificados.Count, impacto);
            return new ResultadoDelta(anadidos.AsReadOnly(), eliminados.AsReadOnly(),
                modificados.AsReadOnly(), impacto);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "No se ha podido comparar el modelo BIM: datos inválidos");
            throw;
        }
        catch (OverflowException ex)
        {
            _logger.LogError(ex, "La valoración BIM supera el intervalo decimal permitido");
            throw new ArgumentException("La medición o su valoración supera el intervalo decimal permitido.",
                nameof(versionNueva), ex);
        }
    }

    private static Dictionary<string, TarifaPartidaBim> IndexarTarifas(
        IEnumerable<TarifaPartidaBim> tarifas, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<string, TarifaPartidaBim>(StringComparer.Ordinal);
        foreach (var tarifa in tarifas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tarifa is null || string.IsNullOrWhiteSpace(tarifa.CodigoPartida))
                throw new ArgumentException("Cada tarifa debe tener un código de partida.", nameof(tarifas));
            if (tarifa.PrecioUnitario < 0 || !Enum.IsDefined(tarifa.Unidad))
                throw new ArgumentException($"La tarifa '{tarifa.CodigoPartida}' no es válida.", nameof(tarifas));
            if (!resultado.TryAdd(tarifa.CodigoPartida, tarifa))
                throw new ArgumentException($"Tarifa duplicada para '{tarifa.CodigoPartida}'.", nameof(tarifas));
        }
        return resultado;
    }

    private static Dictionary<string, ElementoBim> IndexarElementos(IEnumerable<ElementoBim> elementos,
        string parametro, IReadOnlyDictionary<string, TarifaPartidaBim> precios,
        CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<string, ElementoBim>(StringComparer.Ordinal);
        foreach (var elemento in elementos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elemento is null || string.IsNullOrWhiteSpace(elemento.GlobalId))
                throw new ArgumentException("Cada elemento debe tener un GlobalId.", parametro);
            if (!double.IsFinite(elemento.Volumen) || !double.IsFinite(elemento.Area) ||
                elemento.Volumen < 0 || elemento.Area < 0)
                throw new ArgumentException($"Mediciones inválidas en '{elemento.GlobalId}'.", parametro);
            if (string.IsNullOrWhiteSpace(elemento.CodigoPartida) || !precios.ContainsKey(elemento.CodigoPartida))
                throw new ArgumentException($"Falta una tarifa para la partida '{elemento.CodigoPartida}' " +
                    $"del elemento '{elemento.GlobalId}'.", parametro);
            if (!resultado.TryAdd(elemento.GlobalId, elemento))
                throw new ArgumentException($"GlobalId duplicado: '{elemento.GlobalId}'.", parametro);
        }
        return resultado;
    }

    private bool HaCambiado(ElementoBim anterior, ElementoBim actual) =>
        !StringComparer.Ordinal.Equals(anterior.CodigoPartida, actual.CodigoPartida) ||
        SuperaTolerancia(anterior.Volumen, actual.Volumen) ||
        SuperaTolerancia(anterior.Area, actual.Area);

    private bool SuperaTolerancia(double anterior, double actual) =>
        Math.Abs(actual - anterior) > Math.Max(_tolerancia.Absoluta,
            _tolerancia.Relativa * Math.Max(Math.Abs(anterior), Math.Abs(actual)));

    private static decimal Valorar(ElementoBim elemento, IReadOnlyDictionary<string, TarifaPartidaBim> precios)
    {
        var tarifa = precios[elemento.CodigoPartida];
        var cantidad = tarifa.Unidad switch
        {
            UnidadMedicionBim.MetroCubico => checked((decimal)elemento.Volumen),
            UnidadMedicionBim.MetroCuadrado => checked((decimal)elemento.Area),
            UnidadMedicionBim.Unidad => 1m,
            _ => throw new ArgumentOutOfRangeException(nameof(precios), "Unidad BIM no admitida.")
        };
        return checked(cantidad * tarifa.PrecioUnitario);
    }
}
