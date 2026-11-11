using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Importacion;

/// <summary>
/// Paso 1 del Importador (incremento I-2): descarga de la plantilla maestra, subida del .xlsx, validación por hoja y fila,
/// vista previa con el selector Completar/Reemplazar (decisión N14), confirmación e informe. Todo se apoya en el
/// backend de la plantilla maestra: <see cref="AnalizarPlantillaMaestraQuery"/> (no escribe),
/// <see cref="PrevisualizarImportacionMaestraCommand"/> (no escribe; lo que confirmar haría con este modo) y
/// <see cref="EjecutarImportacionMaestraCommand"/>. La autorización es la de esos handlers (Administrador): aquí solo se
/// muestra su mensaje. Tope del fichero: 10 MB (decisión N4), que manda sobre los 20 MB de la maqueta.
/// </summary>
public partial class ImportacionEstructura : ComponentBase, IDisposable
{
    /// <summary>N4: 10 MB por fichero hasta que haya medición (los escaneos de móvil son del Paso 2, no de este).</summary>
    public const long TamanoMaximoBytes = LimitesArchivoSubido.TamanoMaximoBytes;

    // Contrato del archivo y del Historial (HistorialImportacion.Plantilla), no interfaz: no se localizan.
    private const string NombreHistorial = "Maestra";
    private const string HojaEmpresas = "Empresas";
    private const string HojaCentros = "Centros";
    private const string HojaTrabajadores = "Trabajadores";
    private const int MaxFilasEnTabla = 200;
    private const int MaxValoresPreviosEnTabla = 100;

    // Los nombres de hoja de dos palabras o con tilde son el contrato del libro (lo que el lector del backend lee y devuelve en
    // Hoja): viven en el .resx con el mismo valor en es-ES y ca-ES, no se traducen. Evitan texto de interfaz escrito a mano.
    private string HojaClientes => Textos["EstHojaClientes"].Value;
    private string HojaImportacion => Textos["EstHojaImportacion"].Value;

    private string[] OrdenHojas =>
        [HojaEmpresas, "Subcontratas", HojaClientes, HojaCentros, Textos["EstHojaAccesos"].Value, HojaTrabajadores, "Asignaciones",
            Textos["EstHojaVehiculos"].Value, Textos["EstHojaTiposDocumento"].Value];

    /// <summary>Texto de la columna Avisos de la tabla por entidad: el número, o un guion si no hay.</summary>
    private string AvisosOGuion(int avisos) => avisos > 0 ? Textos["EstNAvisos", avisos].Value : Textos["EstSinAvisosGuion"].Value;

    private enum Fase { Subir, Analizando, Validacion, VistaPrevia, Escribiendo, Informe }

    private sealed record HojaVista(string Nombre, bool Pendiente, int FilasDeDatos, int Errores, int Avisos);

    private sealed record FilaEntidad(string ClaveEntidad, string ClaveIdentifica, string Hoja, int Crear, int Actualizar, int SinCambios, int Omitir);

    /// <summary>Si la página puede mostrar el DNI completo (decisión provisional, ver <see cref="MascaraDni"/>); falla cerrado.</summary>
    [Parameter] public bool DniCompleto { get; set; }

    [Inject] private LimitadorAnalisisImportacion Limitador { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<ImportacionEstructura> Logger { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private Fase _fase = Fase.Subir;
    private string? _nombreArchivo;
    private long _tamanoArchivo;
    private byte[]? _contenido;

    // Motivo por el que el archivo no se acepta (extensión, tamaño, lectura) o por el que el análisis no pudo hacerse.
    private string? _rechazo;
    private bool _rechazoEsDelArchivo;

    private PlanImportacionMaestraDto? _plan;
    private IReadOnlyList<HojaVista> _hojas = [];
    private string? _hojaSeleccionada;
    private string _busqueda = string.Empty;

    private bool _reemplazar;
    private ResultadoImportacionMaestraDto? _previa;
    private bool _previaCargando;
    private string? _errorPrevia;

    private bool _mostrarDialogo;
    private bool _importando;
    private string? _errorImportacion;
    private ResultadoImportacionMaestraDto? _resultado;
    private DateTime _momentoInforme;

    // Cada análisis y cada vista previa se numera antes de su primer await: la respuesta que vuelve con un número que ya
    // no es el vigente se tira (un archivo o un modo anterior no pinta ni se confirma bajo el nuevo).
    private int _versionAnalisis;
    private int _versionPrevia;
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationTokenSource? _cicloAnalisis;
    private bool _desechada;

    private static int TamanoMaximoMb => LimitesArchivoSubido.TamanoMaximoMb;

    private bool HayArchivo => _contenido is not null;

    /// <summary>Hay un archivo analizado o a medio camino cuyo resultado todavía no se ha importado: salir lo tira.</summary>
    private bool HayCambiosSinGuardar => _fase is Fase.Validacion or Fase.VistaPrevia or Fase.Escribiendo;

    /// <summary>Paso de la fila de pasos (1 a 6) que corresponde a la fase.</summary>
    private int PasoActual => _fase switch
    {
        Fase.Subir => HayArchivo && !RechazoDelArchivo ? 2 : 1,
        Fase.Analizando or Fase.Validacion => 3,
        Fase.VistaPrevia => 4,
        Fase.Escribiendo => 5,
        _ => 6
    };

    private string ClaveSubtitulo => _fase switch
    {
        Fase.Subir => "EstSubtitulo",
        Fase.Analizando => "EstSubAnalizando",
        Fase.Validacion => "EstSubValidacion",
        Fase.VistaPrevia => "EstSubVistaPrevia",
        Fase.Escribiendo => "EstSubEscribiendo",
        _ => "EstSubInforme"
    };

    private static readonly string[] ClavesPasos = ["EstPasoPlantilla", "EstPasoSubir", "EstPasoValidar", "EstPasoVistaPrevia", "EstPasoConfirmar", "EstPasoInforme"];

    // ---------------------------------------------------------------- subida

    private static string TamanoLegible(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024d)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB"
            : Math.Max(1, (long)Math.Round(bytes / 1024d)).ToString(System.Globalization.CultureInfo.CurrentCulture) + " KB";

    private void Rechazar(string motivo, bool delArchivo = true)
    {
        _contenido = null;
        _nombreArchivo = null;
        _tamanoArchivo = 0;
        _rechazo = motivo;
        _rechazoEsDelArchivo = delArchivo;
    }

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        if (_desechada || _fase is Fase.Analizando or Fase.Escribiendo) return;

        // Un archivo nuevo es un plan nuevo: lo analizado sobre el anterior deja de valer.
        DescartarAnalisis();

        // El puente de arrastrar y soltar entrega la lista entera de archivos soltados, y InputFileChangeEventArgs.File
        // lanza si no hay exactamente uno: se rechaza con aviso, como el tope de archivos de las demás subidas, en vez de
        // dejar escapar la excepción al circuito (revisión Codex de la PR #2164, r2, BAJO 2).
        if (e.FileCount != 1)
        {
            Rechazar(Textos["EstErrorVariosArchivos"].Value);
            return;
        }

        var archivo = e.File;

        if (!archivo.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            Rechazar(Textos["EstErrorExtension"].Value);
            return;
        }

        if (archivo.Size > TamanoMaximoBytes)
        {
            Rechazar(Textos["EstErrorTamano", TamanoMaximoMb].Value);
            return;
        }

        var version = _versionAnalisis;
        try
        {
            await using var flujo = archivo.OpenReadStream(TamanoMaximoBytes, _ciclo.Token);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria, _ciclo.Token);
            if (_desechada || version != _versionAnalisis) return;

            _contenido = memoria.ToArray();
            _nombreArchivo = archivo.Name;
            _tamanoArchivo = archivo.Size;
            _rechazo = null;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // Página retirada.
        }
        catch (IOException)
        {
            // El navegador entrega más de lo declarado: se trata como pasado de tamaño.
            if (version != _versionAnalisis) return;
            Rechazar(Textos["EstErrorTamano", TamanoMaximoMb].Value);
        }
        catch (JSDisconnectedException)
        {
            // El circuito ya no existe: no hay a quién mostrar nada.
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            if (version != _versionAnalisis) return;
            Rechazar(Textos["ErrorLecturaArchivo"].Value);
        }
    }

    // ---------------------------------------------------------------- análisis

    private async Task ValidarArchivoAsync()
    {
        if (_desechada || _contenido is null || _fase != Fase.Subir) return;

        var contenido = _contenido;
        var version = ++_versionAnalisis;
        _rechazo = null;
        _fase = Fase.Analizando;
        StateHasChanged();

        using var cicloAnalisis = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        _cicloAnalisis = cicloAnalisis;
        var token = cicloAnalisis.Token;
        try
        {
            // El tope por Tenant se cuenta contra el Tenant que resuelve el servidor desde la sesión, nunca contra un dato
            // del navegador. Sin Tenant resuelto no se analiza.
            if (TenantActual.TenantId is not { } tenantPropietarioId || tenantPropietarioId == Guid.Empty)
            {
                Volver(version, Textos["ErrorAnalisisSinTenant"].Value, delArchivo: false);
                return;
            }

            using var permiso = await Limitador.IntentarEntrarAsync(tenantPropietarioId, token);
            if (version != _versionAnalisis) return;
            if (permiso is null)
            {
                Volver(version, Textos["ErrorAnalisisOcupado"].Value, delArchivo: false);
                return;
            }

            var resultado = await Mediator.Send(new AnalizarPlantillaMaestraQuery(contenido), token);
            if (version != _versionAnalisis) return;

            if (resultado.EsFallido)
            {
                // Mensaje de Application (p. ej. solo Administrador): no es un problema del archivo.
                Volver(version, resultado.Error.Mensaje, delArchivo: false);
                return;
            }

            _plan = resultado.Valor;
            _hojas = ConstruirHojas(_plan);
            _hojaSeleccionada = (_hojas.FirstOrDefault(h => h.Errores > 0) ?? _hojas.FirstOrDefault(h => h.Avisos > 0) ?? _hojas.FirstOrDefault())?.Nombre;
            _busqueda = string.Empty;
            _fase = Fase.Validacion;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Página retirada o análisis cancelado: no hay a quién pintarlo.
        }
        catch (LimiteHojaImportacionException ex)
        {
            Logger.LogWarning("Archivo de importación rechazado: límite de {Limite} por hoja.", ex.Filas ? "filas" : "celdas");
            Volver(version, Textos[ex.Filas ? "ErrorLimiteFilas" : "ErrorLimiteCeldas"].Value, delArchivo: true);
        }
        catch (ArchivoRechazadoException)
        {
            Logger.LogWarning("Archivo de importación rechazado por el inspector XLSX.");
            Volver(version, Textos["ErrorLecturaArchivo"].Value, delArchivo: true);
        }
        catch (Exception ex)
        {
            // S-D-7: el nombre del archivo puede contener datos personales; al log van solo la extensión y el tamaño.
            Logger.LogError(ex, "Error al analizar un archivo .xlsx de {TamanoBytes} bytes con la plantilla maestra.", _tamanoArchivo);
            Volver(version, Textos["ErrorLecturaArchivo"].Value, delArchivo: true);
        }
        finally
        {
            if (ReferenceEquals(_cicloAnalisis, cicloAnalisis))
                _cicloAnalisis = null;
        }
    }

    /// <summary>Vuelve a la subida con el motivo; un fallo del archivo lo suelta, uno de otro tipo lo conserva para reintentar.</summary>
    private void Volver(int version, string motivo, bool delArchivo)
    {
        if (version != _versionAnalisis || _desechada) return;
        _fase = Fase.Subir;
        _rechazo = motivo;
        _rechazoEsDelArchivo = delArchivo;
        if (delArchivo)
        {
            _contenido = null;
            _nombreArchivo = null;
            _tamanoArchivo = 0;
        }
    }

    private void CancelarAnalisis()
    {
        _cicloAnalisis?.Cancel();
        _versionAnalisis++;
        if (_fase == Fase.Analizando) _fase = Fase.Subir;
    }

    /// <summary>Tira el plan, la vista previa y el resultado y cancela lo que esté en vuelo; deja la subida limpia.</summary>
    private void DescartarAnalisis()
    {
        _cicloAnalisis?.Cancel();
        _versionAnalisis++;
        _versionPrevia++;
        _plan = null;
        _hojas = [];
        _hojaSeleccionada = null;
        _busqueda = string.Empty;
        _previa = null;
        _previaCargando = false;
        _errorPrevia = null;
        _resultado = null;
        _errorImportacion = null;
        _reemplazar = false;
        _mostrarDialogo = false;
        _rechazo = null;
        _contenido = null;
        _nombreArchivo = null;
        _tamanoArchivo = 0;
        _fase = Fase.Subir;
    }

    private void CorregirYSubirDeNuevo() => DescartarAnalisis();

    // ---------------------------------------------------------------- validación

    private IReadOnlyList<HojaVista> ConstruirHojas(PlanImportacionMaestraDto plan)
    {
        int Errores(string hoja) => plan.Omitidos.Count(i => i.Hoja == hoja);
        int Avisos(string hoja) => plan.Advertencias.Count(i => i.Hoja == hoja);

        var datos = new Dictionary<string, int>
        {
            [HojaEmpresas] = plan.Empresas.Count,
            [HojaClientes] = plan.Clientes.Count,
            [HojaCentros] = plan.Centros.Count,
            [HojaTrabajadores] = plan.Trabajadores.Count
        };
        var pendientes = plan.HojasPendientes.ToDictionary(h => h.Hoja, h => h.Filas);

        var hojas = new List<HojaVista>();
        foreach (var nombre in OrdenHojas)
        {
            if (datos.TryGetValue(nombre, out var validas))
                hojas.Add(new HojaVista(nombre, Pendiente: false, validas, Errores(nombre), Avisos(nombre)));
            else if (pendientes.TryGetValue(nombre, out var filas))
                hojas.Add(new HojaVista(nombre, Pendiente: true, filas, Errores(nombre), Avisos(nombre)));
        }

        // Lo que el lector atribuye a otra hoja (hoja no reconocida, el archivo entero) tampoco se pierde.
        foreach (var nombre in plan.Omitidos.Concat(plan.Advertencias).Select(i => i.Hoja).Distinct().Where(n => hojas.All(h => h.Nombre != n)))
            hojas.Add(new HojaVista(nombre, Pendiente: false, 0, Errores(nombre), Avisos(nombre)));

        return hojas;
    }

    private int TotalValidas => _plan is null ? 0 : _plan.Empresas.Count + _plan.Clientes.Count + _plan.Centros.Count + _plan.Trabajadores.Count;

    private int TotalErrores => _plan?.Omitidos.Count ?? 0;

    private int TotalAvisos => _plan?.Advertencias.Count ?? 0;

    private HojaVista? HojaActual => _hojas.FirstOrDefault(h => h.Nombre == _hojaSeleccionada);

    private void SeleccionarHoja(string nombre)
    {
        _hojaSeleccionada = nombre;
        _busqueda = string.Empty;
    }

    private sealed record FilaIncidencia(ItemImportacionDto Item, bool EsError);

    private IReadOnlyList<FilaIncidencia> IncidenciasDeLaHoja()
    {
        if (_plan is null || _hojaSeleccionada is null) return [];
        var todas = _plan.Omitidos.Where(i => i.Hoja == _hojaSeleccionada).Select(i => new FilaIncidencia(i, true))
            .Concat(_plan.Advertencias.Where(i => i.Hoja == _hojaSeleccionada).Select(i => new FilaIncidencia(i, false)));
        if (string.IsNullOrWhiteSpace(_busqueda)) return [.. todas];
        var criterio = _busqueda.Trim();
        return [.. todas.Where(f =>
            f.Item.Descripcion.Contains(criterio, StringComparison.OrdinalIgnoreCase)
            || f.Item.Motivo.Contains(criterio, StringComparison.OrdinalIgnoreCase)
            || f.Item.Fila.ToString().Contains(criterio, StringComparison.Ordinal))];
    }

    private string Enmascarar(string texto) => MascaraDni.Aplicar(texto, DniCompleto);

    // ---------------------------------------------------------------- vista previa

    private async Task ContinuarAVistaPreviaAsync()
    {
        if (_plan is null || TotalValidas == 0) return;
        _fase = Fase.VistaPrevia;
        _errorImportacion = null;
        await CargarVistaPreviaAsync();
    }

    private async Task CambiarModoAsync(bool reemplazar)
    {
        if (_reemplazar == reemplazar || _importando) return;
        _reemplazar = reemplazar;
        await CargarVistaPreviaAsync();
    }

    /// <summary>Pide lo que confirmar haría con este plan y este modo (sin escribir). Solo vale la última respuesta.</summary>
    private async Task CargarVistaPreviaAsync()
    {
        if (_plan is null || _desechada) return;
        var plan = _plan;
        var reemplazar = _reemplazar;
        var version = ++_versionPrevia;
        _previa = null;
        _errorPrevia = null;
        _previaCargando = true;
        StateHasChanged();
        var token = _ciclo.Token;
        try
        {
            var resultado = await Mediator.Send(new PrevisualizarImportacionMaestraCommand(plan, reemplazar), token);
            if (version != _versionPrevia || _desechada) return;
            if (resultado.EsFallido) _errorPrevia = resultado.Error.Mensaje;
            else _previa = resultado.Valor;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al calcular la vista previa de la plantilla maestra.");
            if (version == _versionPrevia && !_desechada) _errorPrevia = Textos["EstErrorPrevia"].Value;
        }
        finally
        {
            if (version == _versionPrevia && !_desechada) _previaCargando = false;
        }
    }

    private void VolverAValidacion()
    {
        _versionPrevia++;
        _previa = null;
        _previaCargando = false;
        _errorPrevia = null;
        _errorImportacion = null;
        _reemplazar = false;
        _fase = Fase.Validacion;
    }

    private static int CreadosDe(ResultadoImportacionMaestraDto r) =>
        r.EmpresasCreadas + r.ClientesCreados + r.RelacionesCreadas + r.CentrosCreados + r.TrabajadoresCreados;

    private static int ActualizadosDe(ResultadoImportacionMaestraDto r) =>
        r.EmpresasActualizadas + r.ClientesActualizados + r.CentrosActualizados + r.TrabajadoresActualizados;

    /// <summary>Confirmación repetida de una operación ya confirmada (ver <see cref="ResultadoImportacionMaestraDto.YaEjecutada"/>).</summary>
    private bool EsYaEjecutada(ResultadoImportacionMaestraDto r) =>
        CreadosDe(r) + ActualizadosDe(r) == 0 && r.Omitidos.Any(o => o.Hoja == HojaImportacion && o.Fila == 0);

    private int OmitidasDe(ResultadoImportacionMaestraDto r, string hoja) => r.Omitidos.Count(o => o.Hoja == hoja);

    /// <summary>
    /// Registros de la hoja que ni se crean, ni se actualizan, ni se omiten. Sale solo del resultado (la vista previa
    /// releída o la escritura), no de <c>YaExiste</c> del análisis, que puede haber quedado obsoleto si otra sesión creó o
    /// dio de baja un registro entre ambos pasos (revisión Codex de la PR #2164, r2, BAJO 1). Del plan solo se toma qué
    /// filas trae la hoja y su clave natural: varias filas con la misma clave (un Cliente empresarial en varias parejas)
    /// son un solo registro.
    /// </summary>
    private int SinCambiosDe(
        ResultadoImportacionMaestraDto r, string hoja, IEnumerable<(int Fila, string Clave)> filas, int creadas, int actualizadas)
    {
        if (EsYaEjecutada(r)) return 0;
        var omitidas = r.Omitidos.Where(o => o.Hoja == hoja).Select(o => o.Fila).ToHashSet();
        var registros = filas.Where(f => !omitidas.Contains(f.Fila)).Select(f => f.Clave).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return Math.Max(0, registros - creadas - actualizadas);
    }

    private IReadOnlyList<FilaEntidad> FilasPorEntidad(ResultadoImportacionMaestraDto r)
    {
        var plan = _plan!;
        return
        [
            new("EstEntEmpresas", "EstIdCif", HojaEmpresas, r.EmpresasCreadas, r.EmpresasActualizadas,
                SinCambiosDe(r, HojaEmpresas, plan.Empresas.Select(e => (e.Fila, e.Cif)), r.EmpresasCreadas, r.EmpresasActualizadas), OmitidasDe(r, HojaEmpresas)),
            new("EstEntClientes", "EstIdCif", HojaClientes, r.ClientesCreados, r.ClientesActualizados,
                SinCambiosDe(r, HojaClientes, plan.Clientes.Select(c => (c.Fila, c.Cif)), r.ClientesCreados, r.ClientesActualizados), OmitidasDe(r, HojaClientes)),
            new("EstEntRelaciones", "EstIdRelacion", string.Empty, r.RelacionesCreadas, 0, 0, 0),
            new("EstEntCentros", "EstIdCentro", HojaCentros, r.CentrosCreados, r.CentrosActualizados,
                SinCambiosDe(r, HojaCentros, plan.Centros.Select(c => (c.Fila, $"{c.CifCliente}|{c.Nombre}")), r.CentrosCreados, r.CentrosActualizados), OmitidasDe(r, HojaCentros)),
            new("EstEntTrabajadores", "EstIdTrabajador", HojaTrabajadores, r.TrabajadoresCreados, r.TrabajadoresActualizados,
                SinCambiosDe(r, HojaTrabajadores, plan.Trabajadores.Select(t => (t.Fila, t.Identidad)), r.TrabajadoresCreados, r.TrabajadoresActualizados), OmitidasDe(r, HojaTrabajadores))
        ];
    }

    private static int AvisosDe(ResultadoImportacionMaestraDto r, string hoja) =>
        hoja.Length == 0 ? 0 : r.Advertencias.Count(a => a.Hoja == hoja);

    // ---------------------------------------------------------------- acción principal (la única etiqueta Primario)

    /// <summary>Hay un rechazo vigente y es del archivo (no de la operación): el archivo ya no sirve. El campo solo vale con un rechazo vigente.</summary>
    private bool RechazoDelArchivo => _rechazo is not null && _rechazoEsDelArchivo;

    /// <summary>Hay algo que continuar: el archivo es analizable, el plan tiene filas válidas o la vista previa ya está calculada.</summary>
    private bool PuedeContinuar => _fase switch
    {
        // Un rechazo que no es del archivo (limitador ocupado, error de Application) conserva el archivo para reintentar.
        Fase.Subir => HayArchivo && !RechazoDelArchivo,
        Fase.Validacion => TotalValidas > 0,
        Fase.VistaPrevia => _previa is not null && !_previaCargando && !_importando,
        _ => true
    };

    private string TextoPrincipal => _fase switch
    {
        Fase.Subir => Textos["EstBotonValidar"].Value,
        Fase.Validacion => TotalErrores > 0 || TotalValidas == 0 ? Textos["EstBotonCorregir"].Value : Textos["EstBotonContinuarVistaPrevia"].Value,
        Fase.VistaPrevia => Textos["EstBotonConfirmar", _previa is null ? 0 : CreadosDe(_previa) + ActualizadosDe(_previa)].Value,
        _ => Textos["EstBotonOtraImportacion"].Value
    };

    private bool PrincipalDeshabilitado => _fase switch
    {
        Fase.Validacion => false,
        _ => !PuedeContinuar
    };

    private Task AccionPrincipalAsync() => _fase switch
    {
        Fase.Subir => ValidarArchivoAsync(),
        Fase.Validacion when TotalErrores > 0 || TotalValidas == 0 => EnElActo(CorregirYSubirDeNuevo),
        Fase.Validacion => ContinuarAVistaPreviaAsync(),
        Fase.VistaPrevia => SolicitarConfirmacionAsync(),
        Fase.Informe => EnElActo(HacerOtraImportacion),
        _ => Task.CompletedTask
    };

    private static Task EnElActo(Action accion)
    {
        accion();
        return Task.CompletedTask;
    }

    private static int SinCambiosTotal(IReadOnlyList<FilaEntidad> filas) => filas.Sum(f => f.SinCambios);

    private static int RegistrosConValorPrevio(ResultadoImportacionMaestraDto r) =>
        r.ValoresSobrescritos.Select(v => (v.Hoja, v.Fila)).Distinct().Count();

    // ---------------------------------------------------------------- confirmación

    private async Task SolicitarConfirmacionAsync()
    {
        if (_previa is null || _previaCargando || _importando) return;
        if (_reemplazar && _previa.ValoresSobrescritos.Count > 0)
        {
            _mostrarDialogo = true;
            return;
        }

        await ConfirmarImportacionAsync();
    }

    private void CambiarVisibilidadDialogo(bool visible) => _mostrarDialogo = visible && !_importando;

    private async Task ConfirmarImportacionAsync()
    {
        // Guarda de doble clic propia, levantada antes del primer await.
        if (_importando || _plan is null || _previa is null || _previaCargando) return;

        // Todo lo que la escritura y su registro en el historial necesitan se fija aquí.
        var plan = _plan;
        var reemplazar = _reemplazar;
        // Lo que se confirma es exactamente la vista previa mostrada (N14): si al escribir ya no coincide, no se escribe.
        var huella = _previa.CalcularHuella();
        var nombreArchivo = string.IsNullOrWhiteSpace(_nombreArchivo) ? "(desconocido)" : _nombreArchivo[..Math.Min(_nombreArchivo.Length, 260)];

        _importando = true;
        _errorImportacion = null;
        _mostrarDialogo = false;
        _fase = Fase.Escribiendo;
        StateHasChanged();

        try
        {
            // La escritura no recibe el token de la página: una escritura ya enviada se deja terminar y registrar.
            var resultado = await Mediator.Send(new EjecutarImportacionMaestraCommand(plan, reemplazar, huella));
            if (resultado.EsFallido)
            {
                await RegistrarFalloAsync(nombreArchivo, resultado.Error.Mensaje);
                if (_desechada) return;
                if (resultado.Error == ErroresImportacionMaestra.VistaPreviaCambiada)
                {
                    // No se ha escrito nada: se recalcula la vista previa y hay que volver a confirmar (y, si ahora hay
                    // valores que se sobrescribirían, pasar por el diálogo de N14).
                    Fallar(Textos["EstVistaPreviaCambiada"].Value);
                    await CargarVistaPreviaAsync();
                    return;
                }
                Fallar(resultado.Error.Mensaje);
                return;
            }

            var r = resultado.Valor;
            await RegistrarExitoAsync(nombreArchivo, CreadosDe(r), r.Advertencias.Count, r.Omitidos.Count);
            if (_desechada) return;
            _resultado = r;
            _momentoInforme = DateTime.UtcNow;
            _fase = Fase.Informe;
            ToastService.Mostrar(Textos["ToastImportacionCompletada"], TonoToast.Exito);
        }
        catch (Exception ex)
        {
            // S-D-7: sin el nombre del archivo en el log (puede contener datos personales).
            Logger.LogError(ex, "Excepción no controlada al importar un archivo .xlsx con la plantilla maestra.");
            await RegistrarFalloAsync(nombreArchivo, "Excepción no controlada durante la importación.");
            if (!_desechada) Fallar(Textos["ErrorImportacion"].Value);
        }
        finally
        {
            if (!_desechada) _importando = false;
        }
    }

    /// <summary>Vuelve a la vista previa con el motivo: confirmar de nuevo es seguro porque la operación es la misma (idempotente).</summary>
    private void Fallar(string mensaje)
    {
        _errorImportacion = mensaje;
        _fase = Fase.VistaPrevia;
    }

    private async Task RegistrarExitoAsync(string nombreArchivo, int creados, int advertencias, int omitidos)
    {
        try
        {
            await Mediator.Send(new RegistrarHistorialImportacionCommand(NombreHistorial, nombreArchivo, true, creados, advertencias, omitidos, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lo escrito ya está: que el Historial no lo registre no deshace ni oculta el informe.
            Logger.LogWarning(ex, "No se pudo registrar la importación de la plantilla maestra en el historial.");
        }
    }

    private async Task RegistrarFalloAsync(string nombreArchivo, string mensaje)
    {
        try
        {
            await Mediator.Send(new RegistrarHistorialImportacionCommand(NombreHistorial, nombreArchivo, false, 0, 0, 0, mensaje));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "No se pudo registrar el fallo de la importación de la plantilla maestra en el historial.");
        }
    }

    private void DescartarImportacion() => DescartarAnalisis();

    // ---------------------------------------------------------------- descargas (CSV)

    private async Task DescargarAsync(string nombre, string csv)
    {
        try
        {
            await using var modulo = await ImportarModuloAsync();
            await modulo.InvokeVoidAsync("descargarTexto", nombre, csv);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            ToastService.Mostrar(Textos["EstErrorDescarga"], TonoToast.Error);
        }
    }

    private async ValueTask<IJSObjectReference> ImportarModuloAsync() =>
        await Js.InvokeAsync<IJSObjectReference>("import", "./js/importacion-descarga.js");

    private Task DescargarErroresAsync()
    {
        if (_plan is null) return Task.CompletedTask;
        var filas = _plan.Omitidos.Select(i => Fila(i, "EstError")).Concat(_plan.Advertencias.Select(i => Fila(i, "EstAviso")));
        return DescargarAsync("errores-importacion.csv", InformeCsv.Construir(CabeceraIncidencias, filas));
    }

    /// <summary>Las filas que la vista previa dice que se omitirán (análisis y escritura), con su motivo, y los avisos.</summary>
    private Task DescargarOmitidasPreviaAsync()
    {
        if (_previa is null) return Task.CompletedTask;
        var filas = _previa.Omitidos.Select(i => Fila(i, "EstError")).Concat(_previa.Advertencias.Select(i => Fila(i, "EstAviso")));
        return DescargarAsync("filas-omitidas-vista-previa.csv", InformeCsv.Construir(CabeceraIncidencias, filas));
    }

    private Task DescargarValoresPreviosAsync()
    {
        if (_previa is null) return Task.CompletedTask;
        var filas = _previa.ValoresSobrescritos.Select(v => (IReadOnlyList<string?>)
            [Enmascarar(v.Hoja), v.Fila.ToString(), v.Campo, Enmascarar(v.ValorPrevio), Enmascarar(v.ValorNuevo ?? string.Empty)]);
        return DescargarAsync("valores-previos-importacion.csv", InformeCsv.Construir(
            [Textos["EstColHoja"].Value, Textos["EstColFila"].Value, Textos["EstColCampo"].Value, Textos["EstColValorPrevio"].Value, Textos["EstColValorNuevo"].Value],
            filas));
    }

    private Task DescargarInformeAsync(bool soloOmitidas)
    {
        if (_resultado is null) return Task.CompletedTask;
        var filas = _resultado.Omitidos.Select(i => Fila(i, "EstError")).ToList();
        if (!soloOmitidas)
        {
            filas.AddRange(_resultado.Advertencias.Select(i => Fila(i, "EstAviso")));
            filas.AddRange(_resultado.ValoresSobrescritos.Select(v => (IReadOnlyList<string?>)
                [Enmascarar(v.Hoja), v.Fila.ToString(), Textos["EstSobrescrito"].Value + ": " + v.Campo, Enmascarar(v.ValorPrevio) + " → " + Enmascarar(v.ValorNuevo ?? string.Empty), Textos["EstSobrescrito"].Value]));
        }

        return DescargarAsync(soloOmitidas ? "filas-omitidas-importacion.csv" : "informe-importacion.csv", InformeCsv.Construir(CabeceraIncidencias, filas));
    }

    private IReadOnlyList<string> CabeceraIncidencias =>
        [Textos["EstColHoja"].Value, Textos["EstColFila"].Value, Textos["EstColElemento"].Value, Textos["EstColMotivo"].Value, Textos["EstColGravedad"].Value];

    private IReadOnlyList<string?> Fila(ItemImportacionDto item, string claveGravedad) =>
        // El nombre de hoja también: una hoja desconocida lleva el nombre que el Excel le dio (revisión Codex #2164, r1, BAJO 2).
        [Enmascarar(item.Hoja), item.Fila == 0 ? string.Empty : item.Fila.ToString(), Enmascarar(item.Descripcion), Enmascarar(item.Motivo), Textos[claveGravedad].Value];

    // ---------------------------------------------------------------- informe

    private void HacerOtraImportacion() => DescartarAnalisis();

    public void Dispose()
    {
        // Las versiones se mueven para que ninguna respuesta que aún llegue se pinte ni vuelva a tocar _ciclo.
        _desechada = true;
        _versionAnalisis++;
        _versionPrevia++;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
