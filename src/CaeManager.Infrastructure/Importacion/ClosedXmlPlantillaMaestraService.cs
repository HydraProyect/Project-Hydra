using CaeManager.Application.Centros;
using CaeManager.Application.Empresas;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Lector de la plantilla maestra del Importador (Paso 1): contrato en
/// Project-Hydra-Negocio/tecnico/importador/LEEME.md. Las columnas se localizan por el NOMBRE de su cabecera (sin el
/// «*» de obligatoria), no por posición, y «Control (no rellenar)» se ignora. Cabecera en la fila 1, ejemplo ficticio
/// en la fila 2 (marca <c>EJEMPLO</c>, que se salta sin registrar: es lo único silencioso, como en las otras plantillas).
///
/// Solo analiza. Cuatro hojas se importan en este incremento (Empresas, Clientes empresariales, Centros,
/// Trabajadores); las demás hojas del libro (Subcontratas, Accesos por Centro, Asignaciones, Vehículos, Tipos de
/// documento) se reconocen y se cuentan en <see cref="PlanImportacionMaestraDto.HojasPendientes"/>, y las columnas con
/// datos que aún no se guardan se anuncian como Advertencia: «nada se descarta en silencio» (DCR-12 B).
/// </summary>
public class ClosedXmlPlantillaMaestraService(
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IOptions<OpcionesLimitesImportacion>? limites = null) : IPlantillaMaestraService
{
    internal const string HojaEmpresas = "Empresas";
    internal const string HojaClientes = "Clientes empresariales";
    internal const string HojaCentros = "Centros";
    internal const string HojaTrabajadores = "Trabajadores";
    private static readonly string[] HojasSinImportar = ["Subcontratas", "Accesos por Centro", "Asignaciones", "Vehículos", "Tipos de documento"];

    private const int PrimeraFilaDatos = 2;
    private const string MarcadorEjemplo = "EJEMPLO";
    private const int MaxNotasCliente = 2000;
    private const string ModalidadConGestion = "Con gestión CAE";
    private const string ModalidadSinGestion = "Sin gestión CAE";

    public async Task<PlanImportacionMaestraDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
    {
        // S-C-4: un .xlsx es un .zip; se inspecciona antes de que ClosedXML lo cargue entero.
        InspectorXlsxSeguro.Verificar(archivo, limites?.Value);

        var empresasExistentes = await empresasContext.Empresas.ToListAsync(cancellationToken);
        var gestionadasPorCif = empresasExistentes.Where(e => e.EsPropia && e.Cif != null)
            .ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var clientesPorCif = empresasExistentes.Where(e => e.EsCritico != null && e.Cif != null)
            .ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var cifsOcupados = new HashSet<string>(empresasExistentes.Where(e => e.Cif != null).Select(e => e.Cif!), StringComparer.OrdinalIgnoreCase);
        var clientePorId = empresasExistentes.ToDictionary(e => e.Id);

        var centrosExistentes = new HashSet<string>(
            (await centrosContext.Centros.Select(c => new { c.ClienteId, c.Nombre }).ToListAsync(cancellationToken))
                .Where(c => clientePorId.TryGetValue(c.ClienteId, out var cl) && cl.Cif != null)
                .Select(c => Clave(clientePorId[c.ClienteId].Cif!, c.Nombre)),
            StringComparer.OrdinalIgnoreCase);

        var identidadesExistentes = new HashSet<string>(
            await trabajadoresContext.Trabajadores.Where(t => t.Dni != null).Select(t => t.Dni!).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        using var libro = new XLWorkbook(archivo);
        var advertencias = new List<ItemImportacionDto>();
        var omitidos = new List<ItemImportacionDto>();

        var empresas = AnalizarEmpresas(libro, gestionadasPorCif, cifsOcupados, advertencias, omitidos);
        var cifsGestionadas = new HashSet<string>(gestionadasPorCif.Keys.Concat(empresas.Select(e => e.Cif)), StringComparer.OrdinalIgnoreCase);

        var clientes = AnalizarClientes(libro, cifsGestionadas, clientesPorCif, cifsOcupados, empresas, advertencias, omitidos);
        var cifsClientes = new HashSet<string>(clientesPorCif.Keys.Concat(clientes.Select(c => c.Cif)), StringComparer.OrdinalIgnoreCase);

        var centros = AnalizarCentros(libro, cifsClientes, cifsGestionadas, centrosExistentes, advertencias, omitidos);
        var trabajadores = AnalizarTrabajadores(libro, cifsGestionadas, identidadesExistentes, advertencias, omitidos);

        var pendientes = new List<HojaPendienteDto>();
        foreach (var nombre in HojasSinImportar)
        {
            if (!libro.Worksheets.TryGetWorksheet(nombre, out var hoja)) continue;
            var filas = new HojaLeida(hoja).Filas().Count();
            if (filas > 0)
                pendientes.Add(new HojaPendienteDto(nombre, filas));
        }

        // Cada análisis es su propia operación: el Id viaja en el plan hasta confirmar.
        return new PlanImportacionMaestraDto(Guid.NewGuid(), empresas, clientes, centros, trabajadores, pendientes, advertencias, omitidos);
    }

    private static string Clave(string cif, string nombre) => cif.Trim().ToUpperInvariant() + "\u001F" + nombre.Trim();

    private static List<EmpresaMaestraDto> AnalizarEmpresas(
        XLWorkbook libro, Dictionary<string, Domain.Empresas.Empresa> gestionadasPorCif, HashSet<string> cifsOcupados,
        List<ItemImportacionDto> advertencias, List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<EmpresaMaestraDto>();
        if (HojaLeida.Abrir(libro, HojaEmpresas, ["Razón social", "CIF"], omitidos) is not { } hoja) return resultado;

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in hoja.Filas())
        {
            var razonSocial = hoja.Texto(fila, "Razón social");
            var cif = Cif(hoja.Texto(fila, "CIF"));
            if (razonSocial is null || cif is null)
            {
                omitidos.Add(new(HojaEmpresas, fila, razonSocial ?? "(sin razón social)", "Faltan datos obligatorios (razón social o CIF)."));
                continue;
            }
            if (!ValidadorIdentificacion.EsIdentificacionFiscalValida(cif))
            {
                omitidos.Add(new(HojaEmpresas, fila, razonSocial, $"La identificación fiscal \"{cif}\" no es válida (CIF, DNI o NIE)."));
                continue;
            }
            if (!vistos.Add(cif))
            {
                omitidos.Add(new(HojaEmpresas, fila, razonSocial, "CIF duplicado dentro del propio archivo."));
                continue;
            }
            var yaExiste = gestionadasPorCif.ContainsKey(cif);
            if (!yaExiste && cifsOcupados.Contains(cif))
            {
                omitidos.Add(new(HojaEmpresas, fila, razonSocial,
                    "Ese CIF ya existe en el Tenant propietario como Cliente empresarial o Subcontrata; no se crea una segunda Empresa gestionada con él."));
                continue;
            }

            var anexo = hoja.SiNo(fila, "Actividad Anexo I (S/N)", out var anexoIlegible);
            if (anexoIlegible)
                advertencias.Add(new(HojaEmpresas, fila, razonSocial, "«Actividad Anexo I (S/N)» solo admite S o N; se importó sin ese dato."));

            resultado.Add(new EmpresaMaestraDto(fila, razonSocial, cif, hoja.Texto(fila, "CNAE"), hoja.Texto(fila, "Convenio aplicable"), anexo, yaExiste));
        }

        hoja.AdvertirColumnasSinImportar(advertencias, HojaEmpresas,
            ["Razón social", "CIF", "CNAE", "Convenio aplicable", "Actividad Anexo I (S/N)"]);
        return resultado;
    }

    private static List<ClienteEmpresarialMaestraDto> AnalizarClientes(
        XLWorkbook libro, HashSet<string> cifsGestionadas, Dictionary<string, Domain.Empresas.Empresa> clientesPorCif,
        HashSet<string> cifsOcupados, List<EmpresaMaestraDto> empresasDelArchivo,
        List<ItemImportacionDto> advertencias, List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<ClienteEmpresarialMaestraDto>();
        if (HojaLeida.Abrir(libro, HojaClientes, ["Razón social", "CIF", "Crítico (C/N)", "CIF de la Empresa gestionada"], omitidos) is not { } hoja)
            return resultado;

        var parVisto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primeraFilaPorCif = new Dictionary<string, ClienteEmpresarialMaestraDto>(StringComparer.OrdinalIgnoreCase);
        var cifsEmpresasGestionadasDelArchivo = new HashSet<string>(empresasDelArchivo.Select(e => e.Cif), StringComparer.OrdinalIgnoreCase);

        foreach (var fila in hoja.Filas())
        {
            var razonSocial = hoja.Texto(fila, "Razón social");
            var cif = Cif(hoja.Texto(fila, "CIF"));
            var cifEmpresa = Cif(hoja.Texto(fila, "CIF de la Empresa gestionada"));
            var critico = hoja.Texto(fila, "Crítico (C/N)")?.ToUpperInvariant();
            var descripcion = razonSocial ?? "(sin razón social)";

            if (razonSocial is null || cif is null || cifEmpresa is null || critico is null)
            {
                omitidos.Add(new(HojaClientes, fila, descripcion, "Faltan datos obligatorios (razón social, CIF, crítico o CIF de la Empresa gestionada)."));
                continue;
            }
            if (critico is not ("C" or "N"))
            {
                omitidos.Add(new(HojaClientes, fila, descripcion, $"«Crítico» solo admite C o N; recibido \"{critico}\"."));
                continue;
            }
            if (!ValidadorIdentificacion.EsIdentificacionFiscalValida(cif))
            {
                omitidos.Add(new(HojaClientes, fila, descripcion, $"La identificación fiscal \"{cif}\" no es válida (CIF, DNI o NIE)."));
                continue;
            }
            if (!cifsGestionadas.Contains(cifEmpresa))
            {
                omitidos.Add(new(HojaClientes, fila, descripcion,
                    $"No se encontró la Empresa gestionada con CIF \"{cifEmpresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }
            if (!parVisto.Add(cif + "\u001F" + cifEmpresa))
            {
                omitidos.Add(new(HojaClientes, fila, descripcion, "Esta pareja Cliente empresarial y Empresa gestionada está repetida dentro del propio archivo."));
                continue;
            }

            var yaExiste = clientesPorCif.ContainsKey(cif);
            // El CIF ya es de una Empresa que no es Cliente empresarial: no se crea un segundo registro con ese CIF
            // (el índice único del Tenant propietario lo rechazaría al guardar).
            if (!yaExiste && (cifsOcupados.Contains(cif) || cifsEmpresasGestionadasDelArchivo.Contains(cif)))
            {
                omitidos.Add(new(HojaClientes, fila, descripcion,
                    "Ese CIF ya pertenece a una Empresa gestionada o a una Subcontrata; no se convierte en Cliente empresarial desde esta hoja."));
                continue;
            }

            if (hoja.Texto(fila, "Nota interna")?.Length > MaxNotasCliente)
            {
                omitidos.Add(new(HojaClientes, fila, descripcion, $"«Nota interna» supera los {MaxNotasCliente} caracteres."));
                continue;
            }

            var dto = new ClienteEmpresarialMaestraDto(fila, razonSocial, cif, critico == "C", cifEmpresa, hoja.Texto(fila, "Nota interna"), yaExiste);
            if (primeraFilaPorCif.TryGetValue(cif, out var primera))
            {
                if (primera.RazonSocial != razonSocial || primera.EsCritico != dto.EsCritico)
                    advertencias.Add(new(HojaClientes, fila, descripcion,
                        $"El mismo CIF aparece en la fila {primera.Fila} con otra razón social o criticidad; se usan los de esa fila."));
            }
            else
            {
                primeraFilaPorCif[cif] = dto;
            }
            resultado.Add(dto);
        }

        hoja.AdvertirColumnasSinImportar(advertencias, HojaClientes,
            ["Razón social", "CIF", "Crítico (C/N)", "CIF de la Empresa gestionada", "Nota interna"]);
        return resultado;
    }

    private static List<CentroMaestraDto> AnalizarCentros(
        XLWorkbook libro, HashSet<string> cifsClientes, HashSet<string> cifsGestionadas, HashSet<string> centrosExistentes,
        List<ItemImportacionDto> advertencias, List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<CentroMaestraDto>();
        if (HojaLeida.Abrir(libro, HojaCentros,
                ["Nombre del Centro", "CIF del Cliente empresarial", "CIF de la Empresa gestionada", "Dirección", "Modalidad de gestión CAE"], omitidos) is not { } hoja)
            return resultado;

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in hoja.Filas())
        {
            var nombre = hoja.Texto(fila, "Nombre del Centro");
            var cifCliente = Cif(hoja.Texto(fila, "CIF del Cliente empresarial"));
            var cifEmpresa = Cif(hoja.Texto(fila, "CIF de la Empresa gestionada"));
            var direccion = hoja.Texto(fila, "Dirección");
            var modalidad = hoja.Texto(fila, "Modalidad de gestión CAE");
            var descripcion = nombre ?? "(sin nombre)";

            if (nombre is null || cifCliente is null || cifEmpresa is null || direccion is null || modalidad is null)
            {
                omitidos.Add(new(HojaCentros, fila, descripcion, "Faltan datos obligatorios (nombre, CIF del Cliente empresarial, CIF de la Empresa gestionada, dirección o modalidad de gestión CAE)."));
                continue;
            }
            if (!modalidad.Equals(ModalidadConGestion, StringComparison.OrdinalIgnoreCase) && !modalidad.Equals(ModalidadSinGestion, StringComparison.OrdinalIgnoreCase))
            {
                omitidos.Add(new(HojaCentros, fila, descripcion, $"«Modalidad de gestión CAE» solo admite \"{ModalidadConGestion}\" o \"{ModalidadSinGestion}\"; recibido \"{modalidad}\"."));
                continue;
            }
            if (!cifsClientes.Contains(cifCliente))
            {
                omitidos.Add(new(HojaCentros, fila, descripcion,
                    $"No se encontró el Cliente empresarial con CIF \"{cifCliente}\" (ni en el sistema ni en la hoja Clientes empresariales de este archivo)."));
                continue;
            }
            if (!cifsGestionadas.Contains(cifEmpresa))
            {
                omitidos.Add(new(HojaCentros, fila, descripcion,
                    $"No se encontró la Empresa gestionada con CIF \"{cifEmpresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }
            var clave = Clave(cifCliente, nombre);
            if (!vistos.Add(clave))
            {
                omitidos.Add(new(HojaCentros, fila, descripcion, "Este Cliente empresarial ya tiene un Centro con este nombre dentro del propio archivo."));
                continue;
            }

            var campoLargo = new[]
            {
                ("Código del Centro", Domain.Centros.Centro.LongitudMaximaCodigo),
                ("Dirección", Domain.Centros.Centro.LongitudMaximaDireccion),
                ("Contacto", Domain.Centros.Centro.LongitudMaximaContacto),
            }.FirstOrDefault(c => hoja.Texto(fila, c.Item1)?.Length > c.Item2).Item1;
            if (campoLargo is not null)
            {
                omitidos.Add(new(HojaCentros, fila, descripcion, $"«{campoLargo}» supera la longitud máxima permitida."));
                continue;
            }

            var contrato = hoja.Fecha(fila, "Contrato vigente hasta", out var contratoIlegible);
            if (contratoIlegible is not null)
                advertencias.Add(new(HojaCentros, fila, descripcion,
                    $"La fecha «{contratoIlegible}» en \"Contrato vigente hasta\" no se pudo interpretar; el Centro se importó sin ese dato."));

            resultado.Add(new CentroMaestraDto(fila, nombre, cifCliente, cifEmpresa, hoja.Texto(fila, "Código del Centro"), direccion,
                hoja.Texto(fila, "Contacto"), contrato, modalidad.Equals(ModalidadSinGestion, StringComparison.OrdinalIgnoreCase),
                centrosExistentes.Contains(clave)));
        }

        hoja.AdvertirColumnasSinImportar(advertencias, HojaCentros,
            ["Nombre del Centro", "CIF del Cliente empresarial", "CIF de la Empresa gestionada", "Código del Centro", "Dirección", "Contacto",
             "Contrato vigente hasta", "Modalidad de gestión CAE"]);
        return resultado;
    }

    private static List<TrabajadorMaestraDto> AnalizarTrabajadores(
        XLWorkbook libro, HashSet<string> cifsGestionadas, HashSet<string> identidadesExistentes,
        List<ItemImportacionDto> advertencias, List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<TrabajadorMaestraDto>();
        string[] obligatorias =
        [
            "Nombre", "Apellidos", "Tipo de identidad", "Número de identidad", "CIF de la Empresa", "Carpeta actual", "Categoría / Puesto",
            "Fecha de nacimiento", "Nacionalidad", "Tipo de contratación", "Fecha de alta en la Empresa",
        ];
        if (HojaLeida.Abrir(libro, HojaTrabajadores, obligatorias, omitidos) is not { } hoja) return resultado;

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in hoja.Filas())
        {
            var nombre = hoja.Texto(fila, "Nombre");
            var apellidos = hoja.Texto(fila, "Apellidos");
            var descripcionBase = $"{nombre} {apellidos}".Trim();
            var descripcion = descripcionBase.Length == 0 ? "(sin nombre)" : descripcionBase;

            var faltan = obligatorias.Where(c => hoja.Texto(fila, c) is null).ToList();
            if (faltan.Count > 0)
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion, $"Faltan datos obligatorios: {string.Join(", ", faltan)}."));
                continue;
            }

            var documento = CriterioDocumentoIdentidadTrabajador.Clasificar(hoja.Texto(fila, "Número de identidad"));
            if (!documento.EsValido)
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion, documento.Error!));
                continue;
            }

            var tipoDeclarado = hoja.Texto(fila, "Tipo de identidad")!;
            var tipoEsperado = documento.Tipo switch { TipoDocumentoTrabajador.Dni => "DNI", TipoDocumentoTrabajador.Nie => "NIE", _ => "Pasaporte" };
            if (!tipoDeclarado.Equals(tipoEsperado, StringComparison.OrdinalIgnoreCase))
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion,
                    $"El tipo de identidad declarado es \"{tipoDeclarado}\" pero el número corresponde a {tipoEsperado}."));
                continue;
            }

            var identidad = documento.Normalizado!;
            if (!vistos.Add(identidad))
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion, "Identidad duplicada dentro del propio archivo."));
                continue;
            }

            var cifEmpresa = Cif(hoja.Texto(fila, "CIF de la Empresa"))!;
            if (!cifsGestionadas.Contains(cifEmpresa))
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion,
                    $"No se encontró la Empresa gestionada con CIF \"{cifEmpresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }

            if (hoja.Texto(fila, "Observaciones")?.Length > Trabajador.LongitudMaximaObservaciones)
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion, "«Observaciones» supera la longitud máxima permitida."));
                continue;
            }

            var nacimiento = hoja.Fecha(fila, "Fecha de nacimiento", out var nacimientoIlegible);
            if (nacimiento is null)
            {
                omitidos.Add(new(HojaTrabajadores, fila, descripcion,
                    nacimientoIlegible is null ? "Falta la fecha de nacimiento." : $"La fecha de nacimiento «{nacimientoIlegible}» no se pudo interpretar."));
                continue;
            }

            if (documento.SeGuardaComoPasaporte)
                advertencias.Add(new(HojaTrabajadores, fila, descripcion, CriterioDocumentoIdentidadTrabajador.AvisoPasaporte + "."));

            resultado.Add(new TrabajadorMaestraDto(fila, nombre!, apellidos!, identidad, cifEmpresa, hoja.Texto(fila, "Carpeta actual")!,
                hoja.Texto(fila, "Categoría / Puesto")!, nacimiento.Value, hoja.Texto(fila, "Correo"), hoja.Texto(fila, "Teléfono"),
                hoja.Texto(fila, "Observaciones"), identidadesExistentes.Contains(identidad)));
        }

        hoja.AdvertirColumnasSinImportar(advertencias, HojaTrabajadores,
            ["Nombre", "Apellidos", "Tipo de identidad", "Número de identidad", "CIF de la Empresa", "Categoría / Puesto", "Fecha de nacimiento",
             "Correo", "Teléfono", "Observaciones"]);
        return resultado;
    }

    private static string? Cif(string? valor) => valor?.Trim().ToUpperInvariant();

    /// <summary>Una hoja del libro, con sus columnas localizadas por el nombre de la cabecera.</summary>
    private sealed class HojaLeida
    {
        private readonly IXLWorksheet _hoja;
        private readonly Dictionary<string, int> _columnas = new(StringComparer.OrdinalIgnoreCase);

        public HojaLeida(IXLWorksheet hoja)
        {
            _hoja = hoja;
            var ultima = hoja.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (var c = 1; c <= ultima; c++)
            {
                var nombre = NombreCabecera(hoja.Cell(1, c).GetString());
                if (nombre.Length > 0 && !nombre.StartsWith("Control (no rellenar)", StringComparison.OrdinalIgnoreCase))
                    _columnas.TryAdd(nombre, c);
            }
        }

        private static string NombreCabecera(string texto) => texto.Trim().TrimEnd('*').Trim();

        public static HojaLeida? Abrir(XLWorkbook libro, string nombre, IEnumerable<string> obligatorias, List<ItemImportacionDto> omitidos)
        {
            if (!libro.Worksheets.TryGetWorksheet(nombre, out var hoja))
            {
                omitidos.Add(new(nombre, 0, "Hoja completa", $"No se encontró la hoja \"{nombre}\" en el archivo."));
                return null;
            }
            var leida = new HojaLeida(hoja);
            var faltan = obligatorias.Where(c => !leida._columnas.ContainsKey(c)).ToList();
            if (faltan.Count == 0) return leida;
            omitidos.Add(new(nombre, 1, "Cabecera", $"Faltan columnas en la hoja \"{nombre}\": {string.Join(", ", faltan)}."));
            return null;
        }

        /// <summary>Filas con datos (desde la 2), sin las de ejemplo y sin las completamente vacías.</summary>
        public IEnumerable<int> Filas()
        {
            var ultima = _hoja.LastRowUsed()?.RowNumber() ?? 0;
            for (var f = PrimeraFilaDatos; f <= ultima; f++)
            {
                var primera = _columnas.Values.Select(c => TextoBruto(f, c)).FirstOrDefault(t => t is not null);
                if (primera is null) continue;
                // Solo la fila 2 con el marcador es el ejemplo de la plantilla; una razón social que empiece por «Ejemplares»
                // u «Ejemplo» en otra fila es un dato real y se importa.
                if (f == PrimeraFilaDatos && primera.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase)
                    && (primera.Length == MarcadorEjemplo.Length || !char.IsLetterOrDigit(primera[MarcadorEjemplo.Length]))) continue;
                yield return f;
            }
        }

        private string? TextoBruto(int fila, int columna)
        {
            var celda = _hoja.Cell(fila, columna);
            if (celda.IsEmpty()) return null;
            var texto = celda.GetString().Trim();
            return texto.Length == 0 ? null : texto;
        }

        public string? Texto(int fila, string columna) => _columnas.TryGetValue(columna, out var c) ? TextoBruto(fila, c) : null;

        /// <summary>S/N; vacío es null. Un valor distinto devuelve null y marca <paramref name="ilegible"/>.</summary>
        public bool? SiNo(int fila, string columna, out bool ilegible)
        {
            ilegible = false;
            var texto = Texto(fila, columna);
            if (texto is null) return null;
            if (texto.Equals("S", StringComparison.OrdinalIgnoreCase)) return true;
            if (texto.Equals("N", StringComparison.OrdinalIgnoreCase)) return false;
            ilegible = true;
            return null;
        }

        /// <summary>Fecha; vacío es null sin aviso; un valor no interpretable devuelve null y su valor bruto.</summary>
        public DateOnly? Fecha(int fila, string columna, out string? valorIlegible)
        {
            valorIlegible = null;
            if (!_columnas.TryGetValue(columna, out var c)) return null;
            var r = FechaCeldaAyudante.Leer(_hoja.Cell(fila, c));
            if (r.Estado == EstadoCeldaFecha.Ilegible) valorIlegible = r.ValorBruto;
            return r.Fecha;
        }

        /// <summary>Anuncia, una vez por hoja, las columnas con datos que este incremento todavía no guarda.</summary>
        public void AdvertirColumnasSinImportar(List<ItemImportacionDto> advertencias, string hoja, IEnumerable<string> importadas)
        {
            var conocidas = new HashSet<string>(importadas, StringComparer.OrdinalIgnoreCase);
            var filas = Filas().ToList();
            var conDatos = _columnas.Where(k => !conocidas.Contains(k.Key) && filas.Any(f => TextoBruto(f, k.Value) is not null))
                .Select(k => k.Key).ToList();
            if (conDatos.Count > 0)
                advertencias.Add(new(hoja, 0, "Columnas con datos aún no importadas",
                    $"Estas columnas traen datos que esta versión del Importador todavía no guarda: {string.Join(", ", conDatos)}."));
        }
    }
}
