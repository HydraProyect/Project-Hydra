using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Importacion.Pages;

public partial class Importacion : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    private const long TamanoMaximoCaeCompletaBytes = 20 * 1024 * 1024;
    private const long TamanoMaximoPlantillaBytes = 5 * 1024 * 1024;
    private const string IdPlantillaClientes = "clientes";

    private sealed record DefinicionPlantilla(
        string Id, string Icono, string Titulo, string Descripcion, string? RutaPlantillaBlanco, long TamanoMaximoBytes,
        string NombreHistorial);

    /// <summary>
    /// Copy y orden fieles al array TEMPLATES del mockup real (Importar
    /// datos TALVEG.dc.html). "cae" es la única sin plantilla en blanco —
    /// decisión ya tomada en la Importacion.razor anterior a este wizard
    /// ("procesa un archivo ya existente", no una plantilla propia) y que
    /// este wizard no cambia, solo hereda.
    /// NombreHistorial es el label corto que exige HistorialImportacion.Plantilla
    /// (varchar(50), ver HistorialImportacionConfiguration) — Titulo es copy de UI
    /// fiel al mockup y no tiene ese límite; "Combinada: Cliente + Empresas +
    /// Centros + Trabajadores" por sí solo ya son 54 caracteres.
    /// </summary>
    private static readonly IReadOnlyList<DefinicionPlantilla> Plantillas =
    [
        new("cae", "CAE", "Importación CAE completa (multi-hoja)",
            "Clientes, empresas, centros, trabajadores y sus documentos en un solo libro (Cuadro de Control CAE). Para arrancar una cartera entera.",
            null, TamanoMaximoCaeCompletaBytes, "CAE completa"),
        // La descripción NO es la del mockup («Solo clientes con sus datos
        // fiscales y de contacto»): la plantilla no recoge CIF, que es el dato
        // fiscal, y por eso EjecutarImportacionCommandHandler omite toda fila
        // cuyo Cliente empresarial o Centro no exista ya. Se dice lo que hace.
        new(IdPlantillaClientes, "CLI", "Plantilla de Clientes",
            "Una fila por nombre de Cliente empresarial y Centro, con criticidad, dirección y contacto. No recoge CIF ni Empresa, así que no da de alta ninguno nuevo.",
            "/clientes/plantilla.xlsx", TamanoMaximoPlantillaBytes, "Clientes"),
        new("combinada", "CMB", "Combinada: Cliente + Empresas + Centros + Trabajadores",
            "Estructura organizativa completa sin documentos. Útil al incorporar un cliente nuevo con su plantilla.",
            "/clientes/plantilla-combinada.xlsx", TamanoMaximoPlantillaBytes, "Combinada"),
        new("documentos", "DOC", "Documentos",
            "Lote de documentos con propietario y tipo por fila. Los PDF se aportan después con subida múltiple.",
            "/documentos/plantilla.xlsx", TamanoMaximoPlantillaBytes, "Documentos")
    ];

    private static readonly IReadOnlyList<string> NombresPasos =
        ["Elegir plantilla", "Analizar", "Revisar plan", "Confirmar", "Reporte"];

    /// <summary>Fila unificada de plan, proyectada desde PlanImportacionDto o PlanImportacionCombinadaDto — ver ProyectarFilas.</summary>
    private sealed record FilaPlan(string Entidad, string Accion, TonoBadge Tono, string Motivo);

    private sealed record ColumnaPlantilla(string Nombre, string Ejemplo, bool Obligatoria);

    /// <summary>
    /// Columnas de la hoja «Clientes», en el orden y con los rótulos que
    /// escribe ClosedXmlPlantillaClientesService.GenerarPlantilla — el test
    /// ImportarClientesGen2Tests las compara con la plantilla real generada,
    /// así que un cambio de un lado sin el otro cae en rojo. Solo el nombre es
    /// obligatorio: el lector corta en la primera fila sin él.
    /// </summary>
    private static readonly IReadOnlyList<ColumnaPlantilla> ColumnasPlantillaClientes =
    [
        new("Cliente / Centro", "Refrielectric S.L.", true),
        new("Crítico (C/N)", "N", false),
        new("Dirección", "Calle Ejemplo 1, Ciudad", false),
        new("Contacto", "Nombre Apellidos — email@ejemplo.com", false)
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ILogger<Importacion> Logger { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "plantilla")]
    private string? PlantillaInicial { get; set; }

    private int _step = 1;
    private int _pasoMaximoAlcanzado = 1;
    private string _plantillaId = "cae";

    /// <summary>
    /// Se llegó desde Clientes (su enlace «Importar clientes», o el marcador
    /// /clientes/importar, que redirige aquí): la página se titula como el
    /// mockup «Importar Clientes» y ofrece la vuelta a Clientes. Es contexto
    /// de navegación, no de autorización.
    /// </summary>
    private bool _desdeClientes;

    // Cada análisis y cada carga del historial se numera antes de su primer
    // await; la respuesta que vuelve con un número que ya no es el vigente se
    // tira. Sin esto, el análisis de un archivo anterior que tarde más que el
    // del siguiente pintaría su plan —y confirmaría ESE plan— bajo el nombre
    // del archivo nuevo.
    private int _versionAnalisis;
    private int _versionHistorial;

    // Cancela lo que la página tenga en vuelo al retirarla (análisis e
    // historial). La importación NO lo recibe: una escritura ya enviada se deja
    // terminar y registrar en el historial en vez de cortarla a medias.
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechada;

    protected override void OnInitialized()
    {
        if (PlantillaInicial is not null && Plantillas.Any(p => p.Id == PlantillaInicial))
            _plantillaId = PlantillaInicial;

        _desdeClientes = PlantillaInicial == IdPlantillaClientes;
    }

    private bool EsPlantillaClientes => _plantillaId == IdPlantillaClientes;

    private string TituloPagina => _desdeClientes && EsPlantillaClientes ? "Importar clientes" : "Importar datos";

    private string? _nombreArchivo;
    private bool _analizando;
    private string? _mensajeError;
    private PlanImportacionDto? _planSimple;
    private PlanImportacionCombinadaDto? _planCombinada;

    private bool _confirmado;
    private bool _reemplazarExistentes;
    private bool _importando;
    private bool _mostrarDialogoImportar;

    private ResultadoImportacionDto? _resultadoSimple;
    private ResultadoImportacionCombinadaDto? _resultadoCombinada;

    private bool _cargandoHistorial = true;
    private IReadOnlyList<HistorialImportacionDto> _historial = [];
    private readonly Dictionary<Guid, string> _usuariosPorId = [];

    private DefinicionPlantilla PlantillaActual => Plantillas.First(p => p.Id == _plantillaId);
    private static readonly string[] MensajesAnalizando =
        ["Leyendo el archivo…", "Comprobando duplicados…", "Validando DNI y CIF…", "Cruzando con la cartera…"];

    protected override Task OnInitializedAsync() => CargarHistorialAsync();

    private async Task CargarHistorialAsync()
    {
        if (_desechada) return;

        var version = ++_versionHistorial;
        _cargandoHistorial = true;
        StateHasChanged();

        try
        {
            var historial = await Mediator.Send(new ObtenerHistorialImportacionesQuery(), _ciclo.Token);
            if (version != _versionHistorial) return;

            var idsFaltantes = historial.Select(h => h.EjecutadaPorUsuarioId).Distinct()
                .Where(id => !_usuariosPorId.ContainsKey(id)).ToList();

            // Mismo patrón que Auditoria.razor.cs: UserManager no pasa por
            // MediatR y compite por el mismo DbContext scoped que el resto
            // del layout.
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                }
            });

            if (version != _versionHistorial) return;
            _historial = historial;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // Página retirada: nadie va a leer el historial.
        }
        catch (Exception)
        {
            if (version == _versionHistorial)
                _historial = [];
        }
        finally
        {
            if (version == _versionHistorial)
                _cargandoHistorial = false;
        }
    }

    private string NombreUsuario(Guid id) => _usuariosPorId.GetValueOrDefault(id, "—");

    private void IrAPaso(int paso)
    {
        // Con la escritura en vuelo el asistente se queda quieto: al volver, el
        // reporte tiene que caer sobre el mismo plan que se confirmó.
        if (_importando || paso > _pasoMaximoAlcanzado) return;
        _step = paso;
    }

    private void SeleccionarPlantilla(string plantillaId)
    {
        if (plantillaId == _plantillaId) return;

        // Un plan pertenece a la plantilla con la que se analizó: cambiarla lo
        // descarta, igual que el progreso que dependía de él. Antes el plan de
        // la plantilla anterior seguía vivo y se podía confirmar —y registrar
        // en el historial— bajo el nombre de la nueva.
        _plantillaId = plantillaId;
        DescartarAnalisis();
        _pasoMaximoAlcanzado = 1;
    }

    /// <summary>Invalida el análisis en vuelo (si lo hay) y todo lo que colgaba de un plan.</summary>
    private void DescartarAnalisis()
    {
        _versionAnalisis++;
        _analizando = false;
        _nombreArchivo = null;
        _mensajeError = null;
        _planSimple = null;
        _planCombinada = null;
        _confirmado = false;
        _reemplazarExistentes = false;
    }

    private void ContinuarAAnalizar()
    {
        _step = 2;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 2);
    }

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        if (_desechada) return;

        var archivo = e.File;
        var plantilla = PlantillaActual;

        // Un archivo nuevo es un plan nuevo: lo revisado y confirmado sobre el
        // anterior deja de valer, y los pasos 3 y 4 se vuelven a ganar.
        DescartarAnalisis();
        var version = _versionAnalisis;
        _pasoMaximoAlcanzado = Math.Min(_pasoMaximoAlcanzado, 2);

        if (archivo.Size > plantilla.TamanoMaximoBytes)
        {
            ToastService.Mostrar($"El archivo no puede superar los {plantilla.TamanoMaximoBytes / (1024 * 1024)} MB.", TonoToast.Error);
            return;
        }

        _analizando = true;
        _nombreArchivo = archivo.Name;
        StateHasChanged();

        try
        {
            await using var flujo = archivo.OpenReadStream(plantilla.TamanoMaximoBytes, _ciclo.Token);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria, _ciclo.Token);
            var contenido = memoria.ToArray();
            if (version != _versionAnalisis) return;

            PlanImportacionDto? planSimple = null;
            PlanImportacionCombinadaDto? planCombinada = null;
            switch (plantilla.Id)
            {
                case "cae":
                    planSimple = await Mediator.Send(new AnalizarImportacionExcelQuery(contenido), _ciclo.Token);
                    break;
                case IdPlantillaClientes:
                    planSimple = await Mediator.Send(new AnalizarPlantillaClientesQuery(contenido), _ciclo.Token);
                    break;
                case "documentos":
                    planSimple = await Mediator.Send(new AnalizarPlantillaDocumentosQuery(contenido), _ciclo.Token);
                    break;
                case "combinada":
                    planCombinada = await Mediator.Send(new AnalizarPlantillaCombinadaQuery(contenido), _ciclo.Token);
                    break;
            }

            if (version != _versionAnalisis) return;
            _planSimple = planSimple;
            _planCombinada = planCombinada;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // Página retirada con el análisis en vuelo: no hay a quién pintarlo.
        }
        catch (Exception ex)
        {
            if (version != _versionAnalisis) return;
            Logger.LogError(ex, "Error al analizar el archivo {NombreArchivo} con la plantilla {PlantillaId}.", archivo.Name, plantilla.Id);
            _mensajeError = "No pudimos leer este archivo. Comprueba que sea el formato de importación de esta plantilla.";
        }
        finally
        {
            if (version == _versionAnalisis)
                _analizando = false;
        }
    }

    private bool TienePlan => _planSimple is not null || _planCombinada is not null;

    private void ContinuarARevisarPlan()
    {
        if (!TienePlan) return;
        _step = 3;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 3);
    }

    private int TotalACrear => _planSimple is { } ps
        ? ps.ClientesCentros.Count(c => !c.YaExisteCliente) + ps.ClientesCentros.Count(c => !c.YaExisteCentro)
            + ps.Empresas.Count(e => !e.YaExiste) + ps.Trabajadores.Count(t => !t.YaExiste)
            + ps.Documentos.Count(d => !d.YaExiste) + ps.Asignaciones.Count(a => !a.YaExiste)
        : _planCombinada is { } pc
            ? pc.Clientes.Count(c => !c.YaExiste) + pc.Empresas.Count(e => !e.YaExiste)
                + pc.Centros.Count(c => !c.YaExiste) + pc.Trabajadores.Count(t => !t.YaExiste)
            : 0;

    private int TotalAdvertencias => _planSimple?.Advertencias.Count ?? _planCombinada?.Advertencias.Count ?? 0;
    private int TotalOmitidos => _planSimple?.Omitidos.Count ?? _planCombinada?.Omitidos.Count ?? 0;

    /// <summary>
    /// Altas de Cliente empresarial o Centro que el plan cuenta en «se
    /// crearán» (las filas «Crear cliente» y «Crear centro») y que la
    /// escritura NO hará. EjecutarImportacionCommandHandler solo reutiliza
    /// Cliente/Centro que ya existían: el Cliente empresarial exige CIF y el
    /// Centro una Empresa, y ni la Plantilla de Clientes ni la CAE completa
    /// recogen esos datos, así que cada una se omite con motivo explícito. El
    /// análisis (AnalizarPlantillaClientesQuery, AnalizarImportacionExcelQuery)
    /// no lo sabe y las sigue contando; esta pantalla no cambia su recuento
    /// —es el del análisis— pero tampoco lo presenta como lo que se escribirá.
    /// Si el handler llega a crearlas, esto y sus textos tienen que irse con él.
    /// </summary>
    private int AltasClienteCentroQueNoSeHaran => _planSimple is { } ps
        ? ps.ClientesCentros.Count(c => !c.YaExisteCliente) + ps.ClientesCentros.Count(c => !c.YaExisteCentro)
        : 0;

    /// <summary>
    /// Filas que acabarán en Omitidos por lo anterior: una por nombre, no una
    /// por alta, porque el handler corta la fila (<c>continue</c>) en cuanto
    /// falta el Cliente empresarial — un nombre nuevo del todo es un único
    /// Omitido aunque cuente como dos altas.
    /// </summary>
    private int FilasClienteCentroQueSeOmitiran =>
        _planSimple?.ClientesCentros.Count(c => !c.YaExisteCliente || !c.YaExisteCentro) ?? 0;

    private string TextoFilasClienteCentroQueSeOmitiran
    {
        get
        {
            var filas = FilasClienteCentroQueSeOmitiran;
            var sujeto = filas == 1
                ? "La fila de Cliente empresarial o Centro con un nombre que todavía no existe se omitirá"
                : $"Las {filas} filas de Cliente empresarial o Centro con un nombre que todavía no existe se omitirán";
            return $"{sujeto}: esta importación no recoge el CIF que exige el alta de un Cliente empresarial ni la Empresa que exige la de un Centro.";
        }
    }

    private string? TituloBadgeCrear => _planSimple is { ClientesCentros.Count: > 0 } ps
        ? $"{ps.ClientesCentros.Count(c => !c.YaExisteCliente)} Clientes empresariales y {ps.ClientesCentros.Count(c => !c.YaExisteCentro)} Centros con nombre nuevo"
        : null;

    private string TituloAvisoAltas => AltasClienteCentroQueNoSeHaran == TotalACrear
        ? "Ninguna de estas altas se hará al importar."
        : $"{AltasClienteCentroQueNoSeHaran} de estas altas no se harán al importar.";

    private string TituloConfirmacion
    {
        get
        {
            var noSeHaran = AltasClienteCentroQueNoSeHaran;
            if (noSeHaran == 0)
                return $"Se crearán {TotalACrear} elementos. Esta acción escribe datos reales.";
            if (noSeHaran == TotalACrear)
                return $"Ninguna de las {TotalACrear} altas del plan se hará.";
            // «Como máximo»: la escritura aún puede omitir filas que dependían
            // de las que no se crean (una Asignación a un Centro nuevo).
            return $"Se crearán como máximo {TotalACrear - noSeHaran} de las {TotalACrear} altas del plan. Esta acción escribe datos reales.";
        }
    }

    private string DetalleConfirmacion => AltasClienteCentroQueNoSeHaran == 0
        ? $"Punto de no retorno. Los {TotalOmitidos} omitidos y {TotalAdvertencias} avisos no se tocan."
        : $"{TextoFilasClienteCentroQueSeOmitiran} Los {TotalOmitidos} omitidos y {TotalAdvertencias} avisos del análisis no se tocan.";

    /// <summary>
    /// Efecto real de confirmar, para el diálogo: lo que se creará según el
    /// plan menos lo que el handler no hará, lo que se omitirá y que no hay
    /// vuelta atrás desde aquí. No promete deshacer: la importación no tiene
    /// operación inversa.
    /// </summary>
    private string MensajeDialogoImportar
    {
        get
        {
            var noSeHaran = AltasClienteCentroQueNoSeHaran;
            var partes = new List<string>
            {
                noSeHaran == 0 ? $"Se crearán {TotalACrear} elementos."
                    : noSeHaran == TotalACrear ? "No se creará ningún elemento."
                    : $"Se crearán como máximo {TotalACrear - noSeHaran} elementos."
            };

            if (noSeHaran > 0)
                partes.Add(TextoFilasClienteCentroQueSeOmitiran);

            if (_planCombinada is not null)
                partes.Add(_reemplazarExistentes
                    ? "En los registros que ya existen se reemplazarán los campos ya rellenados."
                    : "En los registros que ya existen solo se completarán los campos vacíos.");

            if (TotalOmitidos > 0)
                partes.Add(TotalOmitidos == 1
                    ? "La fila que el análisis ya descartó no se toca."
                    : $"Las {TotalOmitidos} filas que el análisis ya descartó no se tocan.");
            partes.Add("Lo que se escriba no se deshace desde esta pantalla; el resultado queda en el historial de importaciones.");

            return string.Join(" ", partes);
        }
    }

    /// <summary>
    /// Une las listas tipadas de cualquiera de los dos DTOs de plan en filas
    /// Crear/Aviso/Omitir homogéneas para la tabla del wizard — el mockup
    /// pide "Fila" (número de fila del Excel), pero eso solo lo llevan
    /// Advertencias/Omitidos hoy (ItemImportacionDto.Fila); las filas "Crear"
    /// no tienen un número de fila real que mostrar en el backend actual, así
    /// que no se inventa uno.
    /// </summary>
    private IReadOnlyList<FilaPlan> ProyectarFilas()
    {
        var filas = new List<FilaPlan>();

        if (_planSimple is { } ps)
        {
            // La Plantilla de Clientes lee la hoja «Clientes»; solo la CAE
            // completa trae Centros_Plataformas.
            var motivoNombreNuevo = EsPlantillaClientes ? "Nombre nuevo en la hoja «Clientes»." : "Nombre nuevo en Centros_Plataformas.";
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCliente).Select(c => new FilaPlan(c.Nombre, "Crear cliente", TonoBadge.Exito, motivoNombreNuevo)));
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCentro).Select(c => new FilaPlan(c.Nombre, "Crear centro", TonoBadge.Exito, motivoNombreNuevo)));
            filas.AddRange(ps.Empresas.Where(e => !e.YaExiste).Select(e => new FilaPlan(e.RazonSocial, "Crear empresa", TonoBadge.Exito, "Razón social nueva.")));
            filas.AddRange(ps.Trabajadores.Where(t => !t.YaExiste).Select(t => new FilaPlan($"{t.Nombre} {t.Apellidos} ({t.Dni})", "Crear trabajador", TonoBadge.Exito, $"DNI nuevo en {t.RazonSocialEmpresa}.")));
            filas.AddRange(ps.Documentos.Where(d => !d.YaExiste).Select(d => new FilaPlan($"{d.Dni} — {d.NombreTipoDocumento}", "Crear documento", TonoBadge.Exito, $"Emitido {d.FechaEmision:dd/MM/yyyy}.")));
            filas.AddRange(ps.Asignaciones.Where(a => !a.YaExiste).Select(a => new FilaPlan($"{a.Dni} → {a.NombreCentro}", "Crear asignación", TonoBadge.Exito, "Asignación nueva.")));
        }
        else if (_planCombinada is { } pc)
        {
            filas.AddRange(pc.Clientes.Where(c => !c.YaExiste).Select(c => new FilaPlan(c.RazonSocial, "Crear cliente", TonoBadge.Exito, $"CIF {c.Cif} nuevo.")));
            filas.AddRange(pc.Empresas.Where(e => !e.YaExiste).Select(e => new FilaPlan(e.RazonSocial, "Crear empresa", TonoBadge.Exito, "Razón social nueva.")));
            filas.AddRange(pc.Centros.Where(c => !c.YaExiste).Select(c => new FilaPlan(c.Nombre, "Crear centro", TonoBadge.Exito, $"Cliente {c.RazonSocialCliente}.")));
            filas.AddRange(pc.Trabajadores.Where(t => !t.YaExiste).Select(t => new FilaPlan($"{t.Nombre} {t.Apellidos} ({t.Dni})", "Crear trabajador", TonoBadge.Exito, $"DNI nuevo en {t.RazonSocialEmpresa}.")));
        }

        var advertencias = _planSimple?.Advertencias ?? _planCombinada?.Advertencias ?? [];
        var omitidos = _planSimple?.Omitidos ?? _planCombinada?.Omitidos ?? [];

        filas.AddRange(advertencias.Select(a => new FilaPlan(a.Descripcion, "Aviso", TonoBadge.Advertencia, a.Motivo)));
        filas.AddRange(omitidos.Select(o => new FilaPlan(o.Descripcion, "Omitir", TonoBadge.Peligro, o.Motivo)));

        return filas;
    }

    private void ContinuarAConfirmar()
    {
        _step = 4;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 4);
    }

    /// <summary>
    /// «Importar ahora» no escribe: abre el diálogo con el efecto real. La
    /// casilla del paso 4 dice que se revisó el plan; el diálogo dice qué va a
    /// pasar al escribirlo, que con estas plantillas no es lo que el plan cuenta.
    /// </summary>
    private void SolicitarImportacion()
    {
        if (!_confirmado || !TienePlan || _importando) return;
        _mensajeError = null;
        _mostrarDialogoImportar = true;
    }

    private void CambiarVisibilidadDialogoImportar(bool visible)
    {
        // Con la escritura en vuelo el diálogo no se cierra (Escape incluido):
        // se cierra solo, al terminar.
        if (!visible && _importando) return;
        _mostrarDialogoImportar = visible;
    }

    private async Task ConfirmarImportacionAsync()
    {
        // Guarda de doble clic propia, levantada antes del primer await: la de
        // DialogoConfirmacion protege su botón, esta protege el comando.
        if (_importando || !_confirmado || !TienePlan) return;

        // Todo lo que la escritura y su registro en el historial necesitan se
        // fija aquí: nada de lo que pase en pantalla mientras tanto lo cambia.
        var planSimple = _planSimple;
        var planCombinada = _planCombinada;
        var reemplazarExistentes = _reemplazarExistentes;
        var plantilla = PlantillaActual;
        var nombreArchivo = _nombreArchivo ?? "(desconocido)";

        _importando = true;
        _mensajeError = null;
        StateHasChanged();

        try
        {
            if (planSimple is not null)
            {
                var resultado = await Mediator.Send(new EjecutarImportacionCommand(planSimple));
                if (resultado.EsFallido)
                {
                    await RegistrarFalloAsync(plantilla, nombreArchivo, resultado.Error.Mensaje);
                    _mensajeError = resultado.Error.Mensaje;
                    return;
                }

                _resultadoSimple = resultado.Valor;
                await RegistrarExitoAsync(plantilla, nombreArchivo,
                    _resultadoSimple.ClientesCreados + _resultadoSimple.CentrosCreados + _resultadoSimple.EmpresasCreadas
                        + _resultadoSimple.TrabajadoresCreados + _resultadoSimple.DocumentosCreados + _resultadoSimple.AsignacionesCreadas,
                    _resultadoSimple.Advertencias.Count, _resultadoSimple.Omitidos.Count);
            }
            else if (planCombinada is not null)
            {
                var resultado = await Mediator.Send(new EjecutarImportacionCombinadaCommand(planCombinada, reemplazarExistentes));
                if (resultado.EsFallido)
                {
                    await RegistrarFalloAsync(plantilla, nombreArchivo, resultado.Error.Mensaje);
                    _mensajeError = resultado.Error.Mensaje;
                    return;
                }

                _resultadoCombinada = resultado.Valor;
                await RegistrarExitoAsync(plantilla, nombreArchivo,
                    _resultadoCombinada.ClientesCreados + _resultadoCombinada.EmpresasCreadas
                        + _resultadoCombinada.CentrosCreados + _resultadoCombinada.TrabajadoresCreados,
                    _resultadoCombinada.Advertencias.Count, _resultadoCombinada.Omitidos.Count);
            }

            ToastService.Mostrar("Importación completada.", TonoToast.Exito);
            _step = 5;
            _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 5);
            await CargarHistorialAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Excepción no controlada al importar el archivo {NombreArchivo} con la plantilla {PlantillaId}.", nombreArchivo, plantilla.Id);
            await RegistrarFalloAsync(plantilla, nombreArchivo, "Excepción no controlada durante la importación.");
            _mensajeError = "No pudimos completar la importación. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _importando = false;
            _mostrarDialogoImportar = false;
        }
    }

    private Task RegistrarExitoAsync(DefinicionPlantilla plantilla, string nombreArchivo, int totalCreados, int totalAdvertencias, int totalOmitidos) =>
        Mediator.Send(new RegistrarHistorialImportacionCommand(
            plantilla.NombreHistorial, nombreArchivo, true, totalCreados, totalAdvertencias, totalOmitidos, null));

    private Task RegistrarFalloAsync(DefinicionPlantilla plantilla, string nombreArchivo, string mensaje) =>
        Mediator.Send(new RegistrarHistorialImportacionCommand(
            plantilla.NombreHistorial, nombreArchivo, false, 0, 0, 0, mensaje));

    private void EmpezarDeNuevo()
    {
        _step = 1;
        _pasoMaximoAlcanzado = 1;
        DescartarAnalisis();
        _resultadoSimple = null;
        _resultadoCombinada = null;
    }

    public void Dispose()
    {
        // Las versiones se mueven para que ninguna respuesta que aún llegue se
        // pinte ni vuelva a tocar _ciclo (Token lanza ya desechado).
        _desechada = true;
        _versionAnalisis++;
        _versionHistorial++;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
