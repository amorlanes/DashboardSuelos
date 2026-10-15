using Constructora.Application.Bim;
using Constructora.Infrastructure.Ifc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Constructora.Infrastructure.Ifc.Tests;

public sealed class ImportadorIfcTests
{
    private readonly ImportadorIfc importer = new(NullLogger<ImportadorIfc>.Instance);

    [Fact]
    public async Task LeeLosDosModelosRealesConIdentidadYUnicode()
    {
        var anterior = await importer.ImportarAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Modelo-v1.ifc"));
        var nuevo = await importer.ImportarAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Modelo-v2.ifc"));
        Assert.Equal("IFC4", anterior.Esquema);
        Assert.Equal(2, anterior.Elementos.Count);
        var muro = anterior.Elementos.Single(x => x.CodigoPartida == "EST-01");
        Assert.Equal("Muro de hormigón M-01", muro.Nombre);
        Assert.Equal("IfcWall", muro.ClaseIfc);
        Assert.Equal(12m, muro.Cantidades.VolumenNeto);
        Assert.Equal(13.2m, muro.Cantidades.VolumenBruto);
        Assert.Equal(40m, muro.Cantidades.AreaNeta);
        Assert.Equal(44m, muro.Cantidades.AreaBruta);
        Assert.Equal(15m, nuevo.Elementos.Single(x => x.GlobalId == muro.GlobalId).Cantidades.VolumenNeto);
        Assert.Empty(anterior.Advertencias);
    }

    [Theory]
    [InlineData("IFC2X3")]
    [InlineData("IFC4")]
    public async Task LeeAmbosEsquemasSinRevit(string schema)
    {
        var resultado = await Leer(Fixture(schema: schema));
        Assert.Equal(schema, resultado.Esquema);
        Assert.Equal(2m, Assert.Single(resultado.Elementos).Cantidades.VolumenNeto);
    }

    [Fact]
    public async Task ConvierteMilimetrosCuadradosYCubicosSinConfundirPotencias()
    {
        var fixture = Fixture(units: SiUnits(".MILLI."), volume: "2000000000.", area: "3000000.", length: "4000.");
        var cantidades = Assert.Single((await Leer(fixture)).Elementos).Cantidades;
        Assert.Equal(2m, cantidades.VolumenNeto);
        Assert.Equal(3m, cantidades.AreaNeta);
        Assert.Equal(4m, cantidades.Longitud);
    }

    [Fact]
    public async Task ConviertePiesPiesCuadradosYPiesCubicosPorConversionBasedUnit()
    {
        var units = """
            #20=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);
            #21=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);
            #22=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);
            #23=IFCDIMENSIONALEXPONENTS(1,0,0,0,0,0,0);
            #24=IFCDIMENSIONALEXPONENTS(2,0,0,0,0,0,0);
            #25=IFCDIMENSIONALEXPONENTS(3,0,0,0,0,0,0);
            #26=IFCMEASUREWITHUNIT(IFCLENGTHMEASURE(0.3048),#20);
            #27=IFCMEASUREWITHUNIT(IFCAREAMEASURE(0.09290304),#21);
            #28=IFCMEASUREWITHUNIT(IFCVOLUMEMEASURE(0.028316846592),#22);
            #29=IFCCONVERSIONBASEDUNIT(#23,.LENGTHUNIT.,'foot',#26);
            #30=IFCCONVERSIONBASEDUNIT(#24,.AREAUNIT.,'square foot',#27);
            #31=IFCCONVERSIONBASEDUNIT(#25,.VOLUMEUNIT.,'cubic foot',#28);
            #40=IFCUNITASSIGNMENT((#29,#30,#31));
            """;
        var cantidades = Assert.Single((await Leer(Fixture(units: units, volume: "10.", area: "10.", length: "10."))).Elementos).Cantidades;
        Assert.Equal(0.283168465920m, cantidades.VolumenNeto);
        Assert.Equal(0.9290304m, cantidades.AreaNeta);
        Assert.Equal(3.048m, cantidades.Longitud);
    }

    [Fact]
    public async Task UnidadPropiaDeCantidadPrevaleceSobreUnidadProyecto()
    {
        var fixture = Fixture(units: SiUnits(".MILLI.") + "\n#41=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);",
            volume: "2.", quantityUnit: "#41");
        var cantidades = Assert.Single((await Leer(fixture)).Elementos).Cantidades;
        Assert.Equal(2m, cantidades.VolumenNeto);
        Assert.Equal(0.000003m, cantidades.AreaNeta);
    }

    [Fact]
    public async Task CantidadesAusentesPermanecenNullYCuentaUnaInstancia()
    {
        var resultado = await Leer(Fixture(includeQuantities: false));
        var cantidades = Assert.Single(resultado.Elementos).Cantidades;
        Assert.Null(cantidades.VolumenNeto);
        Assert.Null(cantidades.VolumenBruto);
        Assert.Null(cantidades.AreaNeta);
        Assert.Null(cantidades.Longitud);
        Assert.Equal(1m, cantidades.Unidades);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Fact]
    public async Task CeroExplicitoSeDistingueDeAusencia()
    {
        var cantidades = Assert.Single((await Leer(Fixture(volume: "0."))).Elementos).Cantidades;
        Assert.Equal(0m, cantidades.VolumenNeto);
        Assert.Null(cantidades.VolumenBruto);
    }

    [Fact]
    public async Task UnidadDesconocidaNoSeAsumeEnMetros()
    {
        const string units = """
            #20=IFCDIMENSIONALEXPONENTS(3,0,0,0,0,0,0);
            #21=IFCCONTEXTDEPENDENTUNIT(#20,.VOLUMEUNIT.,'cubic mysterious');
            #40=IFCUNITASSIGNMENT((#21));
            """;
        var resultado = await Leer(Fixture(units: units));
        Assert.Null(Assert.Single(resultado.Elementos).Cantidades.VolumenNeto);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Fact]
    public async Task UnidadDimensionIncorrectaNoSeAplica()
    {
        var resultado = await Leer(Fixture(quantityUnit: "#20")); // longitud aplicada a volumen
        Assert.Null(Assert.Single(resultado.Elementos).Cantidades.VolumenNeto);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Fact]
    public async Task SinUnidadesNoInventaMediciones()
    {
        var resultado = await Leer(Fixture().Replace(",$,$,$,$,$,#40);", ",$,$,$,$,$,$);", StringComparison.Ordinal));
        var cantidades = Assert.Single(resultado.Elementos).Cantidades;
        Assert.Null(cantidades.VolumenNeto);
        Assert.Null(cantidades.AreaNeta);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Theory]
    [InlineData("CodigoPartida")]
    [InlineData("Codigo_de_partida")]
    [InlineData("CostCode")]
    public async Task ReconoceCodigosPartidaDePropiedades(string name)
    {
        var resultado = await Leer(Fixture(propertyName: name));
        Assert.Equal("EST-01", Assert.Single(resultado.Elementos).CodigoPartida);
    }

    [Fact]
    public async Task LeeTipoYPropiedadesHeredadasSinContarTiposComoElementos()
    {
        var extra = """
            #120=IFCPROPERTYSINGLEVALUE('CodigoPartida',$,IFCLABEL('CER-01'),$);
            #121=IFCPROPERTYSET('0YvctVUKr0kugbFTf53O9P',#5,'Pset_Tipo',$,(#120));
            #122=IFCWALLTYPE('1YvctVUKr0kugbFTf53O9P',#5,'Muro tipo 20cm',$,$,(#121),$,$,$,.STANDARD.);
            #123=IFCRELDEFINESBYTYPE('2YvctVUKr0kugbFTf53O9P',#5,$,$,(#100),#122);
            """;
        var resultado = await Leer(Fixture(includeCode: false, extra: extra));
        var elemento = Assert.Single(resultado.Elementos);
        Assert.Equal("CER-01", elemento.CodigoPartida);
        Assert.Equal("Muro tipo 20cm", elemento.Tipo);
    }

    [Fact]
    public async Task GlobalIdDuplicadoRechazaImportacionCompleta()
    {
        const string extra = "#130=IFCWALL('1YvctVUKr0kugbFTf53O9L',#5,'Duplicado',$,$,$,$,'M2',.STANDARD.);";
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture(extra: extra)));
        Assert.Contains("duplicado", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-1.")]
    [InlineData("1.E309")]
    [InlineData("1.E-400")]
    [InlineData("$")]
    [InlineData("*")]
    public async Task CantidadNegativaONoFinitaNoSeImporta(string volume)
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture(volume: volume)));

    [Fact]
    public async Task SintaxisIFCInvalidaNoSeAceptaParcialmente()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture(volume: "esto_no_es_un_numero")));

    [Fact]
    public async Task ArchivoTruncadoNoSeAcepta()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture().Replace("END-ISO-10303-21;", "", StringComparison.Ordinal)));

    [Fact]
    public async Task SchemaNoSoportadoDevuelveErrorExplicito()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture(schema: "IFC4X3_ADD2")));
        Assert.Contains("Esquema", ex.Message);
    }

    [Fact]
    public async Task RespetaCancelacionAntesDeLeer()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportarAsync("no-importar.ifc", new CancellationToken(true)));

    [Fact]
    public async Task ArchivoQueNoEsIfcNoSeProcesa()
        => await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportarAsync("modelo.rvt"));

    [Fact]
    public async Task ConteosExplicitosIncompatiblesNoSeSustituyenPorUno()
    {
        const string extra = """
            #130=IFCQUANTITYCOUNT('Count',$,$,10.,$);
            #131=IFCQUANTITYCOUNT('Number',$,$,20.,$);
            #132=IFCELEMENTQUANTITY('2YvctVUKr0kugbFTf53O9P',#5,'Qto_Count',$,$,(#130,#131));
            #133=IFCRELDEFINESBYPROPERTIES('3YvctVUKr0kugbFTf53O9P',#5,$,$,(#100),#132);
            """;
        var resultado = await Leer(Fixture(extra: extra));
        Assert.Null(Assert.Single(resultado.Elementos).Cantidades.Unidades);
        Assert.Contains(resultado.Advertencias, x => x.Contains("incompatibles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConteoConUnidadDesconocidaNoSeSustituyePorUno()
    {
        const string extra = """
            #130=IFCQUANTITYCOUNT('Count',$,#20,20.,$);
            #132=IFCELEMENTQUANTITY('2YvctVUKr0kugbFTf53O9P',#5,'Qto_Count',$,$,(#130));
            #133=IFCRELDEFINESBYPROPERTIES('3YvctVUKr0kugbFTf53O9P',#5,$,$,(#100),#132);
            """;
        var resultado = await Leer(Fixture(extra: extra));
        Assert.Null(Assert.Single(resultado.Elementos).Cantidades.Unidades);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Fact]
    public async Task ProyectoConIdentidadInvalidaNoSeImporta()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture().Replace(
            "'0YvctVUKr0kugbFTf53O9L',#5,'Proyecto", "'INVALID',#5,'Proyecto", StringComparison.Ordinal)));

    [Fact]
    public async Task CantidadPositivaMenorQuePrecisionNoSeInventaComoCero()
    {
        var resultado = await Leer(Fixture(volume: "1.E-99"));
        Assert.Null(Assert.Single(resultado.Elementos).Cantidades.VolumenNeto);
        Assert.NotEmpty(resultado.Advertencias);
    }

    [Fact]
    public async Task ArchivoVacioSeRechaza()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(""));

    [Fact]
    public async Task ArchivoMayorQueLimiteSeRechazaAntesDelParser()
    {
        var ruta = Path.Combine(Path.GetTempPath(), "constructora-ifc-grande-" + Guid.NewGuid().ToString("N") + ".ifc");
        try
        {
            using (var stream = File.Create(ruta)) stream.SetLength(ImportadorIfc.TamanoMaximoBytes + 1);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportarAsync(ruta));
            Assert.Contains("256 MiB", ex.Message);
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public async Task DescripcionConComasPuntosComillasYParentesisNoAlteraLectura()
    {
        var fixture = Fixture().Replace("'NetVolume',$,$,2.", "'NetVolume','Texto, ; () y comilla '' IFCQUANTITYVOLUME',$,2.", StringComparison.Ordinal);
        Assert.Equal(2m, Assert.Single((await Leer(fixture)).Elementos).Cantidades.VolumenNeto);
    }

    [Fact]
    public async Task ComentariosStepNoSeInterpretanComoEntidades()
    {
        var fixture = Fixture(extra: "/* #999=IFCQUANTITYVOLUME('NetVolume',$,$,$,$); */");
        Assert.Equal(2m, Assert.Single((await Leer(fixture)).Elementos).Cantidades.VolumenNeto);
    }

    [Fact]
    public async Task ValorNuloConComentarioTampocoSeSustituyePorCero()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Leer(Fixture(volume: "/* cantidad ausente */ $")));

    [Fact]
    public async Task CantidadSinCuartoAtributoNoSeSustituyePorCero()
    {
        var fixture = Fixture().Replace("IFCQUANTITYVOLUME('NetVolume',$,$,2.,$)", "IFCQUANTITYVOLUME('NetVolume',$,$)", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => Leer(fixture));
    }

    private async Task<ImportacionIfcDto> Leer(string contenido)
    {
        var ruta = Path.Combine(Path.GetTempPath(), "constructora-ifc-test-" + Guid.NewGuid().ToString("N") + ".ifc");
        try
        {
            await File.WriteAllTextAsync(ruta, contenido);
            return await importer.ImportarAsync(ruta);
        }
        finally { File.Delete(ruta); }
    }

    private static string SiUnits(string prefix = "$") => $$"""
        #20=IFCSIUNIT(*,.LENGTHUNIT.,{{prefix}},.METRE.);
        #21=IFCSIUNIT(*,.AREAUNIT.,{{prefix}},.SQUARE_METRE.);
        #22=IFCSIUNIT(*,.VOLUMEUNIT.,{{prefix}},.CUBIC_METRE.);
        #40=IFCUNITASSIGNMENT((#20,#21,#22));
        """;

    private static string Fixture(string schema = "IFC4", string? units = null, string volume = "2.", string area = "3.",
        string length = "4.", bool includeQuantities = true, string propertyName = "CodigoPartida", string quantityUnit = "$",
        bool includeCode = true, string extra = "")
    {
        var isIfc2 = schema == "IFC2X3";
        var quantityFormula = isIfc2 ? "" : ",$";
        var quantities = includeQuantities ? $$"""
            #101=IFCQUANTITYVOLUME('NetVolume',$,{{quantityUnit}},{{volume}}{{quantityFormula}});
            #102=IFCQUANTITYAREA('NetSideArea',$,$,{{area}}{{quantityFormula}});
            #103=IFCQUANTITYLENGTH('Length',$,$,{{length}}{{quantityFormula}});
            #104=IFCELEMENTQUANTITY('2YvctVUKr0kugbFTf53O9L',#5,'Qto_WallBaseQuantities',$,$,(#101,#102,#103));
            #105=IFCRELDEFINESBYPROPERTIES('3YvctVUKr0kugbFTf53O9L',#5,$,$,(#100),#104);
            """ : "";
        var code = includeCode ? $$"""
            #110=IFCPROPERTYSINGLEVALUE('{{propertyName}}',$,IFCLABEL('EST-01'),$);
            #111=IFCPROPERTYSET('0YvctVUKr0kugbFTf53O9M',#5,'Pset_Constructora',$,(#110));
            #112=IFCRELDEFINESBYPROPERTIES('1YvctVUKr0kugbFTf53O9M',#5,$,$,(#100),#111);
            """ : "";
        return $$"""
            ISO-10303-21;
            HEADER;
            FILE_DESCRIPTION(('Fixture IFC real; cantidades sin geometria'),'2;1');
            FILE_NAME('fixture.ifc','2026-10-15T00:00:00',('Constructora'),('Constructora'),'Tests','Tests','');
            FILE_SCHEMA(('{{schema}}'));
            ENDSEC;
            DATA;
            #1=IFCPERSON($,$,'Test',$,$,$,$,$);
            #2=IFCORGANIZATION($,'Constructora',$,$,$);
            #3=IFCPERSONANDORGANIZATION(#1,#2,$);
            #4=IFCAPPLICATION(#2,'1.0','Tests IFC','TEST');
            #5=IFCOWNERHISTORY(#3,#4,$,.ADDED.,$,$,$,0);
            {{units ?? SiUnits()}}
            #50=IFCPROJECT('0YvctVUKr0kugbFTf53O9L',#5,'Proyecto prueba',$,$,$,$,$,#40);
            #100=IFCWALL('1YvctVUKr0kugbFTf53O9L',#5,'Muro prueba',$,$,$,$,'M1'{{(isIfc2 ? "" : ",.STANDARD.")}});
            {{quantities}}
            {{code}}
            {{extra}}
            ENDSEC;
            END-ISO-10303-21;
            """;
    }
}
