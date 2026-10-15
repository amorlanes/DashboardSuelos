using Constructora.Application;
using Xunit;

namespace Constructora.Application.Tests;

public sealed class DeltaScreeningServiceTests
{
    private static readonly TarifaPartidaBim[] Tarifas =
    [
        new("HORM", UnidadMedicionBim.MetroCubico, 100m),
        new("PINT", UnidadMedicionBim.MetroCuadrado, 10m),
        new("PUER", UnidadMedicionBim.Unidad, 300m)
    ];

    [Fact]
    public void Comparar_ValoraAltasBajasCambiosYReasignacionDePartida()
    {
        ElementoBim[] anterior =
        [
            Elemento("mismo", "HORM", 10, 20),
            Elemento("baja", "PUER", 0, 0),
            Elemento("cambio", "HORM", 2, 30),
            Elemento("reasignado", "HORM", 1, 12)
        ];
        ElementoBim[] nuevo =
        [
            Elemento("mismo", "HORM", 10, 20),
            Elemento("alta", "PINT", 0, 5),
            Elemento("cambio", "HORM", 3, 30),
            Elemento("reasignado", "PINT", 1, 12)
        ];

        var resultado = new DeltaScreeningService().Comparar(anterior, nuevo, Tarifas);

        Assert.Equal("alta", Assert.Single(resultado.ElementosAnadidos).GlobalId);
        Assert.Equal("baja", Assert.Single(resultado.ElementosEliminados).GlobalId);
        Assert.Equal(2, resultado.ElementosModificados.Count);
        Assert.Equal(-130m, resultado.ImpactoEconomicoEstimado); // +50 -300 +100 +(120-100)
        var reasignado = Assert.Single(resultado.ElementosModificados, x => x.Nuevo.GlobalId == "reasignado");
        Assert.Equal("HORM", reasignado.Anterior.CodigoPartida);
        Assert.Equal("PINT", reasignado.Nuevo.CodigoPartida);
    }

    [Theory]
    [InlineData(1, 1.0000001)]
    [InlineData(1000000, 1000000.5)]
    [InlineData(0, 0.0000001)]
    public void Comparar_IgnoraRuidoDentroDeLaToleranciaAbsolutaORelativa(double antes, double despues)
    {
        var resultado = new DeltaScreeningService().Comparar(
            [Elemento("A", "HORM", antes, antes)], [Elemento("A", "HORM", despues, despues)], Tarifas);

        Assert.Empty(resultado.ElementosModificados);
        Assert.Equal(0m, resultado.ImpactoEconomicoEstimado);
    }

    [Fact]
    public void Comparar_DetectaCambioDeAreaYNoUsaElVolumenParaTarifaSuperficial()
    {
        var resultado = new DeltaScreeningService().Comparar(
            [Elemento("A", "PINT", 100, 2)], [Elemento("A", "PINT", 100, 3)], Tarifas);

        Assert.Single(resultado.ElementosModificados);
        Assert.Equal(10m, resultado.ImpactoEconomicoEstimado);
    }

    [Fact]
    public void Comparar_UsaPrecioPorUnidadIndependientementeDeLaGeometria()
    {
        var resultado = new DeltaScreeningService().Comparar([], [Elemento("A", "PUER", 20, 30)], Tarifas);

        Assert.Equal(300m, resultado.ImpactoEconomicoEstimado);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Comparar_RechazaGlobalIdDuplicadoEnCualquierVersion(bool duplicadoEnAnterior)
    {
        ElementoBim[] duplicados = [Elemento("A"), Elemento("A")];
        Assert.Throws<ArgumentException>(() => new DeltaScreeningService().Comparar(
            duplicadoEnAnterior ? duplicados : [], duplicadoEnAnterior ? [] : duplicados, Tarifas));
    }

    [Fact]
    public void Comparar_RechazaClavesVacias()
    {
        Assert.Throws<ArgumentException>(() => new DeltaScreeningService().Comparar([], [Elemento(" ")], Tarifas));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void Comparar_RechazaGeometriaInvalida(double medida)
    {
        var servicio = new DeltaScreeningService();
        Assert.Throws<ArgumentException>(() => servicio.Comparar([], [Elemento("A", volumen: medida)], Tarifas));
        Assert.Throws<ArgumentException>(() => servicio.Comparar([], [Elemento("A", area: medida)], Tarifas));
    }

    [Fact]
    public void Comparar_RechazaTarifaAusenteInclusoSiNoHayCambios()
    {
        var elemento = Elemento("A", "DESCONOCIDA");
        Assert.Throws<ArgumentException>(() => new DeltaScreeningService().Comparar([elemento], [elemento], Tarifas));
    }

    [Fact]
    public void Comparar_RechazaTarifasDuplicadasONegativas()
    {
        var servicio = new DeltaScreeningService();
        Assert.Throws<ArgumentException>(() => servicio.Comparar([], [], [Tarifas[0], Tarifas[0]]));
        Assert.Throws<ArgumentException>(() => servicio.Comparar([], [], [Tarifas[0] with { PrecioUnitario = -1 }]));
    }

    [Fact]
    public void Comparar_RechazaDesbordamientoDeValoracionSinDevolverUnCosteIncorrecto()
    {
        Assert.Throws<ArgumentException>(() => new DeltaScreeningService().Comparar([], [Elemento("A", volumen: double.MaxValue)], Tarifas));
    }

    [Fact]
    public void Comparar_RespetaCancelacion()
    {
        using var cancelacion = new CancellationTokenSource();
        cancelacion.Cancel();
        Assert.Throws<OperationCanceledException>(() => new DeltaScreeningService().Comparar(
            [], [Elemento("A")], Tarifas, cancelacion.Token));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(-1, 0)]
    public void Constructor_RechazaToleranciasInvalidas(double absoluta, double relativa)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaScreeningService(
            tolerancia: new ToleranciaGeometricaBim(absoluta, relativa)));
    }

    private static ElementoBim Elemento(string id, string partida = "HORM", double volumen = 1, double area = 1) =>
        new(id, "Estructura", "Genérico", volumen, area, partida);
}
