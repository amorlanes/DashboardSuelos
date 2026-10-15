using System.Globalization;
using System.Text;
using Constructora.Application.Bim;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xbim.Common;
using Xbim.Common.Step21;
using Xbim.Ifc4.Interfaces;
using Xbim.IO;
using Xbim.IO.Memory;

namespace Constructora.Infrastructure.Ifc;

/// <summary>Lee IFC STEP local sin Revit, red, geometría nativa ni credenciales.</summary>
public sealed class ImportadorIfc(ILogger<ImportadorIfc> logger) : IImportadorIfc
{
    public const long TamanoMaximoBytes = 256L * 1024 * 1024;

    public Task<ImportacionIfcDto> ImportarAsync(string rutaArchivo, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rutaArchivo);
        // xBIM realiza parsing síncrono; nunca se ejecuta en el hilo de presentación.
        return Task.Run(() => Importar(rutaArchivo, cancellationToken), cancellationToken);
    }

    private ImportacionIfcDto Importar(string ruta, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(ruta), ".ifc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selecciona un archivo IFC STEP con extensión .ifc.");
            using var stream = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > TamanoMaximoBytes)
                throw new InvalidDataException("El IFC debe contener datos y tener como máximo 256 MiB.");
            ValidarEnvolturaStep(stream);
            ValidarValoresObligatorios(stream, ct);
            var provider = new MemoryModelProvider(NullLoggerFactory.Instance);
            var schema = provider.GetXbimSchemaVersion(ruta);
            if (schema is not (XbimSchemaVersion.Ifc2X3 or XbimSchemaVersion.Ifc4 or XbimSchemaVersion.Ifc4x1))
                throw new InvalidDataException("Esquema IFC no admitido. Se admiten IFC2x3, IFC4 e IFC4x1 STEP.");
            using var model = (MemoryModel)provider.Create(schema, XbimStoreType.InMemoryModel);
            model.AllowMissingReferences = false;
            var errores = model.LoadStep21(stream, stream.Length,
                (_, _) => ct.ThrowIfCancellationRequested());
            ct.ThrowIfCancellationRequested();
            if (errores != 0)
                throw new InvalidDataException($"El archivo IFC tiene {errores} errores de sintaxis o referencias; no se ha importado parcialmente.");
            var proyectos = model.Instances.OfType<IIfcProject>().ToArray();
            if (proyectos.Length != 1)
                throw new InvalidDataException("El archivo debe contener exactamente un IfcProject para identificar sus unidades.");
            var proyecto = proyectos[0];
            ValidarGlobalId(proyecto.GlobalId.ToString(), proyecto.EntityLabel);
            var avisos = new HashSet<string>(StringComparer.Ordinal);
            var unidades = new UnidadesIfc(proyecto.UnitsInContext, avisos);
            var datos = IndexarPropiedades(model, ct);
            var tipos = IndexarTipos(model, ct);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var elementos = new List<ElementoModeloBimDto>();
            foreach (var element in model.Instances.OfType<IIfcElement>())
            {
                ct.ThrowIfCancellationRequested();
                var id = element.GlobalId.ToString();
                ValidarGlobalId(id, element.EntityLabel);
                if (!ids.Add(id)) throw new InvalidDataException($"GlobalId duplicado: {id}. No se puede mantener su identidad entre versiones.");
                var clase = element.ExpressType.Name;
                tipos.TryGetValue(element.EntityLabel, out var tipo);
                datos.TryGetValue(element.EntityLabel, out var sets);
                var setsTipo = tipo?.HasPropertySets ?? Enumerable.Empty<IIfcPropertySetDefinition>();
                var propiedades = (sets ?? []).Concat(setsTipo).OfType<IIfcPropertySet>().ToArray();
                var codigo = Propiedad(propiedades, "CODIGOPARTIDA", "CODIGODEPARTIDA", "COSTCODE");
                var familia = Propiedad(propiedades, "FAMILIA", "FAMILY", "FAMILYNAME")
                    ?? element.ObjectType?.ToString() ?? clase;
                var nombreTipo = tipo?.Name?.ToString() ?? element.ObjectType?.ToString() ?? clase;
                var cantidades = LeerCantidades(sets ?? [], unidades, id, avisos, ct);
                if (cantidades.VolumenNeto is null && cantidades.VolumenBruto is null && cantidades.AreaNeta is null
                    && cantidades.AreaBruta is null && cantidades.Longitud is null)
                    avisos.Add($"{id}: no hay cantidades dimensionales reconocidas. No se deducen de la geometría.");
                elementos.Add(new(id, clase, element.Name?.ToString() ?? id, familia, nombreTipo, codigo, cantidades));
            }
            if (elementos.Count == 0) avisos.Add("El IFC no contiene instancias IfcElement; no hay elementos para vincular.");
            logger.LogInformation("IFC {Archivo} leído: {Elementos} elementos, {Advertencias} advertencias, esquema {Esquema}",
                Path.GetFileName(ruta), elementos.Count, avisos.Count, schema);
            return new(schema == XbimSchemaVersion.Ifc2X3 ? "IFC2X3" : schema == XbimSchemaVersion.Ifc4x1 ? "IFC4X1" : "IFC4",
                proyecto.GlobalId.ToString(), elementos, avisos.Order(StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "No se pudo importar el IFC {Archivo}", Path.GetFileName(ruta));
            if (ex is InvalidDataException or IOException or UnauthorizedAccessException) throw;
            throw new InvalidDataException("No se puede leer el IFC. Comprueba que sea un archivo STEP IFC2x3 o IFC4 válido.", ex);
        }
    }

    private static void ValidarEnvolturaStep(FileStream stream)
    {
        // La cabecera y el cierre evitan aceptar un archivo truncado que el parser tolere.
        var inicio = new byte[(int)Math.Min(4096, stream.Length)];
        stream.ReadExactly(inicio);
        if (!Encoding.UTF8.GetString(inicio).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("ISO-10303-21;", StringComparison.Ordinal))
            throw new InvalidDataException("El archivo no tiene cabecera ISO-10303-21 STEP.");
        stream.Position = Math.Max(0, stream.Length - 4096);
        var final = new byte[(int)(stream.Length - stream.Position)];
        stream.ReadExactly(final);
        if (!Encoding.UTF8.GetString(final).TrimEnd().EndsWith("END-ISO-10303-21;", StringComparison.Ordinal))
            throw new InvalidDataException("El IFC está truncado: falta END-ISO-10303-21.");
        stream.Position = 0;
    }

    private static void ValidarValoresObligatorios(FileStream stream, CancellationToken ct)
    {
        // xBIM convierte algunos valores obligatorios STEP '$' a default(double)=0.
        // Este control léxico acotado comprueba sólo el cuarto atributo numérico de
        // las cantidades simples; xBIM sigue siendo el lector de entidades y relaciones.
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var prefijo = new StringBuilder();
        var numero = new StringBuilder();
        var profundidad = 0;
        var atributo = 0;
        var cantidad = false;
        var cadena = false;
        var comentario = false;
        var caracteres = 0;
        int leido;
        while ((leido = reader.Read()) != -1)
        {
            if ((++caracteres & 4095) == 0) ct.ThrowIfCancellationRequested();
            var c = (char)leido;
            if (comentario)
            {
                if (c == '*' && reader.Peek() == '/') { reader.Read(); comentario = false; }
                continue;
            }
            if (cadena)
            {
                if (c != '\'') continue;
                if (reader.Peek() == '\'') { reader.Read(); continue; }
                cadena = false;
                continue;
            }
            if (c == '/' && reader.Peek() == '*') { reader.Read(); comentario = true; continue; }
            if (c == '\'')
            {
                cadena = true;
                if (cantidad && atributo == 4) numero.Append(c);
                continue;
            }
            if (c == ';')
            {
                prefijo.Clear(); numero.Clear(); profundidad = 0; atributo = 0; cantidad = false;
                continue;
            }
            if (c == '(')
            {
                if (profundidad == 0)
                {
                    var texto = prefijo.ToString();
                    var separador = texto.IndexOf('=');
                    var clase = separador < 0 ? "" : texto[(separador + 1)..];
                    cantidad = clase.Equals("IFCQUANTITYVOLUME", StringComparison.OrdinalIgnoreCase)
                        || clase.Equals("IFCQUANTITYAREA", StringComparison.OrdinalIgnoreCase)
                        || clase.Equals("IFCQUANTITYLENGTH", StringComparison.OrdinalIgnoreCase)
                        || clase.Equals("IFCQUANTITYCOUNT", StringComparison.OrdinalIgnoreCase);
                    atributo = 1;
                }
                else if (cantidad && atributo == 4) numero.Append(c);
                profundidad++;
                continue;
            }
            if (profundidad == 1 && cantidad && c is ',' or ')')
            {
                if (c == ')' && atributo < 4)
                    throw new InvalidDataException("El IFC contiene una cantidad incompleta: falta su atributo numérico obligatorio.");
                if (atributo == 4)
                {
                    var literal = numero.ToString();
                    if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor)
                        || !double.IsFinite(valor) || valor < 0
                        || (valor == 0 && literal.TakeWhile(x => x is not ('e' or 'E')).Any(x => x is >= '1' and <= '9')))
                        throw new InvalidDataException("El IFC contiene una cantidad obligatoria ausente, negativa o no finita; no se sustituye por cero.");
                    numero.Clear();
                }
                atributo++;
            }
            if (c == ')') { profundidad--; continue; }
            if (profundidad == 0 && !char.IsWhiteSpace(c) && prefijo.Length < 256) prefijo.Append(c);
            if (cantidad && atributo == 4 && profundidad >= 1 && c != ',' && !char.IsWhiteSpace(c))
            {
                if (numero.Length >= 128) throw new InvalidDataException("Valor numérico IFC fuera de rango.");
                numero.Append(c);
            }
        }
        stream.Position = 0;
    }

    private static Dictionary<int, List<IIfcPropertySetDefinition>> IndexarPropiedades(IModel model, CancellationToken ct)
    {
        var resultado = new Dictionary<int, List<IIfcPropertySetDefinition>>();
        foreach (var rel in model.Instances.OfType<IIfcRelDefinesByProperties>())
        {
            ct.ThrowIfCancellationRequested();
            var definitions = rel.RelatingPropertyDefinition?.PropertySetDefinitions.ToArray() ?? [];
            foreach (var objeto in rel.RelatedObjects)
            {
                if (!resultado.TryGetValue(objeto.EntityLabel, out var lista)) resultado[objeto.EntityLabel] = lista = [];
                lista.AddRange(definitions);
            }
        }
        return resultado;
    }

    private static Dictionary<int, IIfcTypeObject> IndexarTipos(IModel model, CancellationToken ct)
    {
        var resultado = new Dictionary<int, IIfcTypeObject>();
        foreach (var rel in model.Instances.OfType<IIfcRelDefinesByType>())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var objeto in rel.RelatedObjects)
            {
                if (resultado.TryGetValue(objeto.EntityLabel, out var anterior) && anterior.EntityLabel != rel.RelatingType.EntityLabel)
                    throw new InvalidDataException($"La entidad #{objeto.EntityLabel} tiene varios tipos IFC incompatibles.");
                resultado[objeto.EntityLabel] = rel.RelatingType;
            }
        }
        return resultado;
    }

    private static string? Propiedad(IEnumerable<IIfcPropertySet> sets, params string[] nombres)
    {
        foreach (var set in sets)
        foreach (var prop in set.HasProperties.OfType<IIfcPropertySingleValue>())
        {
            if (!nombres.Contains(Normalizar(prop.Name.ToString()), StringComparer.Ordinal)) continue;
            var texto = prop.NominalValue?.Value?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(texto)) return texto;
        }
        return null;
    }

    private static void ValidarGlobalId(string id, int etiqueta)
    {
        if (id.Length != 22 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '$')) || id[0] is < '0' or > '3')
            throw new InvalidDataException($"GlobalId IFC inválido en entidad #{etiqueta}.");
    }

    private static string Normalizar(string texto) => new(texto.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static CantidadesIfcDto LeerCantidades(IEnumerable<IIfcPropertySetDefinition> sets, UnidadesIfc unidades,
        string id, HashSet<string> avisos, CancellationToken ct)
    {
        var valores = new Dictionary<string, decimal>();
        var tieneConteoExplicito = false;
        foreach (var set in sets.OfType<IIfcElementQuantity>().DistinctBy(x => x.EntityLabel))
        foreach (var qty in set.Quantities)
        {
            ct.ThrowIfCancellationRequested();
            var nombre = Normalizar(qty.Name.ToString());
            (string Clave, int Dimension, double Valor)? dato = qty switch
            {
                IIfcQuantityVolume volumen when nombre == "NETVOLUME" => ("VN", 3, volumen.VolumeValue),
                IIfcQuantityVolume volumen when nombre == "GROSSVOLUME" => ("VB", 3, volumen.VolumeValue),
                IIfcQuantityArea area when nombre is "NETAREA" or "NETSIDEAREA" or "NETFLOORAREA" or "NETSURFACEAREA" => ("AN", 2, area.AreaValue),
                IIfcQuantityArea area when nombre is "GROSSAREA" or "GROSSSIDEAREA" or "GROSSFLOORAREA" or "GROSSSURFACEAREA" => ("AB", 2, area.AreaValue),
                IIfcQuantityLength longitud when nombre == "LENGTH" => ("L", 1, longitud.LengthValue),
                IIfcQuantityCount cuenta when nombre is "COUNT" or "NUMBEROFITEMS" or "NUMBER" => ("U", 0, cuenta.CountValue),
                _ => null
            };
            if (dato is null) continue;
            var (clave, dimension, valor) = dato.Value;
            if (clave == "U") tieneConteoExplicito = true;
            if (!double.IsFinite(valor) || valor < 0)
                throw new InvalidDataException($"{id}: cantidad {qty.Name} negativa o no finita.");
            var factor = unidades.Factor((qty as IIfcPhysicalSimpleQuantity)?.Unit, dimension);
            if (factor is null)
            {
                avisos.Add($"{id}: {qty.Name} no se importa porque su unidad no es conocida o falta la unidad de proyecto.");
                continue;
            }
            decimal convertido;
            try { convertido = checked((decimal)valor * factor.Value); }
            catch (OverflowException ex) { throw new InvalidDataException($"{id}: cantidad {qty.Name} fuera de rango decimal.", ex); }
            if (valor > 0 && convertido == 0)
            {
                avisos.Add($"{id}: {qty.Name} es menor que la precisión decimal admitida y queda sin medir.");
                continue;
            }
            if (valores.TryGetValue(clave, out var existente) && existente != convertido)
            {
                // Dos áreas netas distintas (por ejemplo lateral y suelo) no son intercambiables.
                valores.Remove(clave);
                avisos.Add($"{id}: varias cantidades {clave} incompatibles; se requiere elegir una medición en el modelo de origen.");
                valores["AMB" + clave] = 1;
            }
            else if (!valores.ContainsKey("AMB" + clave)) valores[clave] = convertido;
        }
        // Cada IfcElement leído representa una instancia. El recuento no procede de su geometría.
        return new(valores.GetValueOrDefaultNullable("VN"), valores.GetValueOrDefaultNullable("VB"),
            valores.GetValueOrDefaultNullable("AN"), valores.GetValueOrDefaultNullable("AB"),
            valores.GetValueOrDefaultNullable("L"), tieneConteoExplicito ? valores.GetValueOrDefaultNullable("U") : 1m);
    }

    private sealed class UnidadesIfc
    {
        private readonly Dictionary<int, IIfcNamedUnit?> proyecto = [];
        private readonly HashSet<string> avisos;

        public UnidadesIfc(IIfcUnitAssignment? assignment, HashSet<string> avisos)
        {
            this.avisos = avisos;
            foreach (var unidad in assignment?.Units.OfType<IIfcNamedUnit>() ?? [])
            {
                var dim = unidad.UnitType switch { IfcUnitEnum.LENGTHUNIT => 1, IfcUnitEnum.AREAUNIT => 2, IfcUnitEnum.VOLUMEUNIT => 3, _ => -1 };
                if (dim == -1) continue;
                if (!proyecto.TryAdd(dim, unidad))
                {
                    proyecto[dim] = null;
                    avisos.Add($"Hay varias unidades de proyecto para dimensión {dim}; las cantidades sin unidad explícita quedan sin medir.");
                }
            }
        }

        public decimal? Factor(IIfcNamedUnit? unidad, int dimension)
        {
            if (dimension == 0) return unidad is null ? 1m : null;
            unidad ??= proyecto.GetValueOrDefault(dimension);
            return unidad is null ? null : Resolver(unidad, dimension, new HashSet<int>());
        }

        private decimal? Resolver(IIfcUnit unidad, int dimension, HashSet<int> visitadas)
        {
            if (unidad is not IIfcNamedUnit named || !visitadas.Add(named.EntityLabel) || visitadas.Count > 16) return null;
            var esperada = dimension switch { 1 => IfcUnitEnum.LENGTHUNIT, 2 => IfcUnitEnum.AREAUNIT, _ => IfcUnitEnum.VOLUMEUNIT };
            if (named.UnitType != esperada) return null;
            if (unidad is IIfcSIUnit si)
            {
                var nombre = dimension switch { 1 => IfcSIUnitName.METRE, 2 => IfcSIUnitName.SQUARE_METRE, _ => IfcSIUnitName.CUBIC_METRE };
                if (si.Name != nombre) return null;
                var prefijo = si.Prefix?.ToString() ?? "";
                var potencia = prefijo switch
                {
                    "" => 0, "EXA" => 18, "PETA" => 15, "TERA" => 12, "GIGA" => 9, "MEGA" => 6, "KILO" => 3,
                    "HECTO" => 2, "DECA" => 1, "DECI" => -1, "CENTI" => -2, "MILLI" => -3, "MICRO" => -6,
                    "NANO" => -9, "PICO" => -12, "FEMTO" => -15, "ATTO" => -18, _ => int.MinValue
                };
                if (potencia == int.MinValue) return null;
                try
                {
                    decimal factor = 1m;
                    for (var i = 0; i < Math.Abs(potencia * dimension); i++) factor = checked(potencia >= 0 ? factor * 10m : factor / 10m);
                    return factor > 0 ? factor : null;
                }
                catch (OverflowException) { return null; }
            }
            if (unidad is IIfcConversionBasedUnitWithOffset) return null;
            if (unidad is IIfcConversionBasedUnit conversion)
            {
                var conversionFactor = conversion.ConversionFactor;
                var basis = Resolver(conversionFactor.UnitComponent, dimension, visitadas);
                if (basis is null) return null;
                double valor;
                try { valor = Convert.ToDouble(conversionFactor.ValueComponent.Value, CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return null; }
                if (!double.IsFinite(valor) || valor <= 0) return null;
                try
                {
                    var factor = checked((decimal)valor * basis.Value);
                    return factor > 0 ? factor : null;
                }
                catch (OverflowException) { return null; }
            }
            avisos.Add($"Unidad IFC no admitida: {named.Symbol}. Las cantidades afectadas quedan sin medir.");
            return null;
        }
    }
}

internal static class CantidadesDictionaryExtensions
{
    public static decimal? GetValueOrDefaultNullable(this Dictionary<string, decimal> source, string key)
        => source.TryGetValue(key, out var value) ? value : null;
}
