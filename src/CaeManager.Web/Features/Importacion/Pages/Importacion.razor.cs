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
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Importacion.Pages;

public partial class Importacion : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    private const long TamanoMaximoCaeCompletaBytes = 20 * 1024 * 1024;
    private const long TamanoMaximoPlantillaBytes = 5 * 1024 * 1024;
    private const string IdPlantillaClientes = "clientes";
    private const string IdPlantillaCombinada = "combinada";

    /// <param name="NombreIcono">Icono del catálogo (Icono.razor) de la tarjeta, el del mockup.</param>
    /// <param name="NombreCorto">
    /// Nombre de la plantilla en «Continuar con …»: el mockup lo corta en el
    /// primer « (» o «:» (<c>title.split(' (')[0].split(':')[0]</c>). La zona
    /// de soltar sigue usando <see cref="DefinicionPlantilla.Titulo"/> entero,
    /// como pinta el mockup «Importar Combinado».
    /// </param>
    private sealed record DefinicionPlantilla(
        string Id, string NombreIcono, string Titulo, string NombreCorto, string Descripcion, string? RutaPlantillaBlanco,
        long TamanoMaximoBytes, string NombreHistorial);

    /// <summary>
    /// Copy y orden fieles al array TEMPLATES del mockup real (Importar
    /// datos TALVEG.dc.html). "cae" es la única sin plantilla en blanco —
    /// decisión ya tomada en la Importacion.razor anterior a este wizard
    /// ("procesa un archivo ya existente", no una plantilla propia) y que
    /// este wizard no cambia, solo hereda. Su descripción tampoco es ya la
    /// del mockup — ver el comentario junto a "cae" — por el mismo motivo
    /// que la de Clientes: dice lo que la plantilla hace hoy, no lo que
    /// hacía antes de Fase 10.
    /// NombreHistorial es el label corto que exige HistorialImportacion.Plantilla
    /// (varchar(50), ver HistorialImportacionConfiguration) — Titulo es copy de UI
    /// fiel al mockup y no tiene ese límite; "Combinada: Cliente + Empresas +
    /// Centros + Trabajadores" por sí solo ya son 54 caracteres.
    /// </summary>
    private static readonly IReadOnlyList<DefinicionPlantilla> Plantillas =
    [
        // La descripción NO es la del mockup («Para arrancar una cartera
        // entera»): Centros_Plataformas es a la vez Cliente y Centro (ver
        // ClosedXmlImportacionParser), y desde Fase 10 Cliente exige CIF y
        // Centro exige Empresa — datos que este libro no recoge. Por eso
        // EjecutarImportacionCommandHandler nunca da de alta un Cliente
        // empresarial ni un Centro nuevos desde aquí, solo reutiliza los que
        // ya existan (mismo motivo que la Plantilla de Clientes, más abajo).
        // "CAE completa" reimporta una cartera CAE que ya existe en otro
        // sitio, no da de alta una cartera nueva — para eso está "combinada",
        // la única de las cuatro que sí recoge CIF y Empresa. El recuento del
        // paso "Revisar plan" sigue sin corregirse para este formato (a
        // diferencia de Clientes): AltasClienteCentroQueNoSeHaran avisa de
        // cuántas de sus altas no se harán, pero las cuenta igual en
        // TotalACrear — brecha registrada como REC-106
        // (Project-Hydra-Negocio/tecnico/reconciliacion/REGISTRO-REC.md),
        // NOT READY porque su cierre exige extender el contrato de
        // PlanImportacionDto sin romper el emparejamiento posicional de
        // Centros_Plataformas con Asignaciones ni la causal de Asignación
        // que exige DCR-12 (ver IMPORTACION.md § 3 bis).
        new("cae", "importar", "Importación CAE completa (multi-hoja)", "Importación CAE completa",
            "Clientes, empresas, centros, trabajadores y sus documentos en un solo libro (Cuadro de Control CAE) que ya existe. No da de alta ningún Cliente empresarial ni Centro nuevos — solo reutiliza los que ya existan. Para incorporar un Cliente empresarial nuevo con su estructura completa, usa Combinada.",
            null, TamanoMaximoCaeCompletaBytes, "CAE completa"),
        // La descripción NO es la del mockup («Solo clientes con sus datos
        // fiscales y de contacto»): la plantilla no recoge CIF, que es el dato
        // fiscal, y por eso EjecutarImportacionCommandHandler omite toda fila
        // cuyo Cliente empresarial o Centro no exista ya. Se dice lo que hace.
        new(IdPlantillaClientes, "clientes", "Plantilla de Clientes", "Plantilla de Clientes",
            "Una fila por nombre de Cliente empresarial y Centro, con criticidad, dirección y contacto. No recoge CIF ni Empresa, así que no da de alta ninguno nuevo.",
            "/clientes/plantilla.xlsx", TamanoMaximoPlantillaBytes, "Clientes"),
        // El título conserva «Cliente» a secas: es el nombre que pinta el
        // mockup «Importar Combinado» en la zona de soltar y el que busca el
        // E2E. Es deuda terminológica (la hoja «Clientes» crea Empresas en
        // papel de Cliente empresarial), no un concepto nuevo.
        new(IdPlantillaCombinada, "empresas", "Combinada: Cliente + Empresas + Centros + Trabajadores", "Combinada",
            "Estructura organizativa completa sin documentos. Útil al incorporar un Cliente empresarial nuevo con su plantilla.",
            "/clientes/plantilla-combinada.xlsx", TamanoMaximoPlantillaBytes, "Combinada"),
        new("documentos", "documentos", "Documentos", "Documentos",
            "Lote de documentos con propietario y tipo por fila. Los PDF se aportan después con subida múltiple.",
            "/documentos/plantilla.xlsx", TamanoMaximoPlantillaBytes, "Documentos")
    ];

    private sealed record HojaCombinada(int Numero, string Nombre, IReadOnlyList<string> Columnas, string Regla);

    /// <summary>
    /// Las cuatro hojas de la Combinada en el orden en que las lee
    /// ClosedXmlPlantillaCombinadaService.AnalizarAsync (Clientes → Empresas →
    /// Centros → Trabajadores), con los rótulos de cabecera que escribe su
    /// GenerarPlantilla —el test ImportarDatosGen2 las compara con la plantilla
    /// real generada— y la regla que el lector aplica a cada una: CIF válido
    /// (AnalizarClientes), asociación descartada con aviso y Empresa creada
    /// igual (AnalizarEmpresas), Cliente y Empresa obligatorios y resueltos
    /// (AnalizarCentros), documento con dígito de control y Empresa resuelta
    /// (AnalizarTrabajadores). Si el lector cambia una regla, este texto va con él.
    /// </summary>
    private static readonly IReadOnlyList<HojaCombinada> HojasCombinada =
    [
        new(1, "Clientes", ["Razón social", "CIF", "Crítico (C/N)"],
            "Cada fila es un Cliente empresarial. Sin CIF válido, la fila se omite entera."),
        new(2, "Empresas", ["Razón social", "Clientes asociados (separados por ;)"],
            "Cada Cliente empresarial citado se busca en el sistema y en la hoja 1. Si no aparece, esa asociación se descarta con un aviso y la Empresa se crea igual."),
        new(3, "Centros", ["Nombre", "Cliente", "Empresa", "Código", "Dirección", "Contacto", "Contrato vigente hasta"],
            "Cliente y Empresa son obligatorios y tienen que existir en el sistema o en las hojas 1 y 2."),
        new(4, "Trabajadores", ["Nombre", "Apellidos", "DNI", "Empresa", "Fecha de nacimiento", "Email"],
            "DNI, NIE o CIF con dígito de control válido. La Empresa tiene que existir en el sistema o en la hoja 2.")
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
    /// Plantilla con la que se llegó desde Clientes: sus enlaces «Importar
    /// clientes» e «Importación combinada», o los marcadores
    /// /clientes/importar y /clientes/importar-combinado, que redirigen aquí
    /// (H-1). Con ella la página ofrece la vuelta a Clientes y, mientras siga
    /// elegida, se titula como su mockup («Importar Clientes» o «Importar
    /// Combinado»). <c>null</c> si no se llegó desde Clientes. Es contexto de
    /// navegación, no de autorización.
    /// </summary>
    private string? _plantillaDesdeClientes;

    private bool DesdeClientes => _plantillaDesdeClientes is not null;

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

        if (PlantillaInicial is IdPlantillaClientes or IdPlantillaCombinada)
            _plantillaDesdeClientes = PlantillaInicial;
    }

    private bool EsPlantillaClientes => _plantillaId == IdPlantillaClientes;

    private bool EsPlantillaCombinada => _plantillaId == IdPlantillaCombinada;

    /// <summary>
    /// Entradilla del mockup «Importar Combinado», con el apellido que el
    /// mockup no pone: lo que crea la hoja «Clientes» son Clientes empresariales.
    /// </summary>
    private static readonly RenderFragment EntradillaCombinada = builder => builder.AddContent(0,
        "Estructura organizativa completa sin documentos: Clientes empresariales, Empresas, Centros y Trabajadores en un solo libro de cuatro hojas. Útil al incorporar un Cliente empresarial nuevo con su plantilla.");

    private string TextoSinNadaNuevo
    {
        get
        {
            var n = TotalReutilizados;
            var sujeto = n == 1 ? "El único registro del archivo ya existe" : $"Los {n} registros del archivo ya existen";
            return $"{sujeto} en el sistema. No es un archivo vacío: es una importación que no añadiría nada. " +
                "Si lo que quieres es actualizarlos, marca «Reemplazar los campos ya rellenados» en el paso siguiente.";
        }
    }

    private string ClaseVistaPlan(bool arbol) =>
        arbol == _vistaArbolPlan ? "vista-plan-importacion vista-plan-importacion-activa" : "vista-plan-importacion";

    /// <summary>Se llegó desde Clientes con la Combinada y sigue elegida: cabecera del mockup «Importar Combinado».</summary>
    private bool EsCombinadaDesdeClientes => _plantillaDesdeClientes == IdPlantillaCombinada && _plantillaId == IdPlantillaCombinada;

    private string TituloPagina =>
        _plantillaDesdeClientes is not null && _plantillaDesdeClientes == _plantillaId
            ? _plantillaId == IdPlantillaClientes ? "Importar clientes" : "Importación combinada"
            : "Importar datos";

    // Mismas vistas del paso 3 que el mockup «Importar Combinado»: el árbol
    // de lo que se va a crear (por defecto) o la lista plana de ProyectarFilas.
    private bool _vistaArbolPlan = true;

    /// <summary>
    /// Registros del plan de la Combinada que ya existen (el YaExiste del DTO,
    /// en sus cuatro listas): no son altas ni descartes, se reutilizan y, sin
    /// «Reemplazar», solo se completa lo vacío. Las plantillas simples no los
    /// cuentan: ninguna actualiza nada.
    /// </summary>
    private int TotalReutilizados => _planCombinada is { } pc
        ? pc.Clientes.Count(c => c.YaExiste) + pc.Empresas.Count(e => e.YaExiste)
            + pc.Centros.Count(c => c.YaExiste) + pc.Trabajadores.Count(t => t.YaExiste)
        : 0;

    /// <summary>
    /// El archivo se leyó entero y todo lo que trae ya existe: ni altas, ni
    /// avisos, ni omitidos. No es un archivo vacío (eso daría 0 reutilizados).
    /// </summary>
    private bool CombinadaSinNadaNuevo => _planCombinada is not null && TotalACrear == 0
        && TotalAdvertencias == 0 && TotalOmitidos == 0 && TotalReutilizados > 0;

    /// <summary>
    /// Alcance real de «Reemplazar» según EjecutarImportacionCombinadaCommandHandler:
    /// un dato del archivo distinto sustituye al que había (ResolverTexto, y
    /// en el Cliente empresarial razón social y Crítico tal como vengan); las
    /// asociaciones Empresa↔Cliente empresarial que su celda no nombre se
    /// cierran, también con la celda vacía; y un texto o fecha en blanco no
    /// borra lo que había. El mockup decía solo lo último.
    /// </summary>
    private string TextoAvisoReemplazar
    {
        get
        {
            var n = TotalReutilizados;
            if (n == 0)
                return "Ningún registro de este archivo existe todavía, así que marcarla no cambia nada.";

            var sujeto = n == 1 ? "el registro que ya existía toma" : $"los {n} registros que ya existían toman";
            return $"Con esta casilla marcada, {sujeto} lo que traiga el archivo: un dato distinto sustituye al que había, " +
                "y la razón social y la marca Crítico de cada Cliente empresarial se toman tal como vengan. En cada Empresa, " +
                "las asociaciones con Clientes empresariales que su celda no nombre se cierran, también si la celda viene vacía. " +
                "Un texto o una fecha que el archivo deje en blanco no borra lo que había.";
        }
    }

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
        // Se toma aquí, con la página viva: tras Dispose, _ciclo.Token lanza
        // ObjectDisposedException, pero este token ya tomado sigue diciendo
        // que se canceló.
        var token = _ciclo.Token;
        _cargandoHistorial = true;
        StateHasChanged();

        try
        {
            var historial = await Mediator.Send(new ObtenerHistorialImportacionesQuery(), token);
            if (version != _versionHistorial) return;

            var idsFaltantes = historial.Select(h => h.EjecutadaPorUsuarioId).Distinct()
                .Where(id => !_usuariosPorId.ContainsKey(id)).ToList();

            // Mismo patrón que Auditoria.razor.cs: UserManager no pasa por
            // MediatR y compite por el mismo DbContext scoped que el resto
            // del layout. La espera de la puerta sí se cancela con el token;
            // UserManager.FindByIdAsync no admite uno (usa su propio
            // CancellationToken, None), así que la búsqueda en curso termina
            // y lo que se corta es la siguiente.
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                    token.ThrowIfCancellationRequested();
                }
            }, token);

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

    // --- Plantillas como radiogroup de WAI-ARIA (mismo patrón que Reportes) ---

    private readonly ElementReference[] _referenciasPlantillas = new ElementReference[Plantillas.Count];

    // La plantilla que una flecha acaba de marcar y que aún no tiene el foco.
    // El foco se da DESPUÉS del render, cuando ya lleva tabindex="0": darlo
    // antes lo pondría en un botón que el propio render está a punto de cambiar.
    private int? _plantillaPorEnfocar;

    /// <summary>
    /// Flechas circulares en el orden de la rejilla: abajo/derecha a la
    /// siguiente, arriba/izquierda a la anterior. Marcar pasa por
    /// <see cref="SeleccionarPlantilla"/>, así que descarta el plan igual que
    /// el clic. Enter y Espacio no se tratan aquí: la opción es un
    /// <c>&lt;button&gt;</c> y el navegador los convierte en su clic.
    /// </summary>
    private void ManejarTeclaPlantilla(KeyboardEventArgs e, int indiceActual)
    {
        var destino = e.Key switch
        {
            "ArrowDown" or "ArrowRight" => (indiceActual + 1) % Plantillas.Count,
            "ArrowUp" or "ArrowLeft" => (indiceActual - 1 + Plantillas.Count) % Plantillas.Count,
            _ => -1
        };

        if (destino < 0) return;

        SeleccionarPlantilla(Plantillas[destino].Id);
        _plantillaPorEnfocar = destino;
    }

    // Al cambiar de paso, el control que tenía el foco («Continuar…», «Ver
    // plan…», el paso del indicador) desaparece con el paso anterior: sin
    // moverlo, teclado y lector de pantalla se quedan sin contexto. Cada paso
    // pinta un único <h2> con esta referencia (la cadena if/else de _step), y
    // el foco va a él cuando el paso pintado ya no es el último enfocado. El
    // paso inicial cuenta como enfocado: al entrar no se roba el foco.
    private ElementReference _tituloPaso;
    private int _pasoEnfocado = 1;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_step != _pasoEnfocado)
        {
            _pasoEnfocado = _step;
            await _tituloPaso.FocusAsync();
        }

        if (_plantillaPorEnfocar is not { } indice) return;

        _plantillaPorEnfocar = null;
        await _referenciasPlantillas[indice].FocusAsync();
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

    /// <summary>
    /// Ámbar en vez de verde cuando el plan ya sabe que alguna de sus altas de
    /// Cliente empresarial o Centro no se hará (ver
    /// <see cref="AltasClienteCentroQueNoSeHaran"/>): la cifra de
    /// <see cref="TotalACrear"/> sigue siendo la del análisis —corregirla es
    /// trabajo de REC-106, no de esta pantalla—, pero el color no debe leer
    /// como éxito garantizado algo que ya se sabe que no lo es del todo.
    /// </summary>
    private TonoBadge TonoBadgeCrear => AltasClienteCentroQueNoSeHaran > 0 ? TonoBadge.Advertencia : TonoBadge.Exito;

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
            // Ámbar, no verde: por construcción (ver AltasClienteCentroQueNoSeHaran)
            // toda fila que cae en estos dos Where es una que
            // EjecutarImportacionCommandHandler nunca creará — el análisis la
            // sigue contando en TotalACrear (REC-106), pero la fila no debe
            // pintarse como una alta ya garantizada.
            var motivoNombreNuevo = EsPlantillaClientes ? "Nombre nuevo en la hoja «Clientes»." : "Nombre nuevo en Centros_Plataformas.";
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCliente).Select(c => new FilaPlan(c.Nombre, "Crear cliente", TonoBadge.Advertencia, motivoNombreNuevo)));
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCentro).Select(c => new FilaPlan(c.Nombre, "Crear centro", TonoBadge.Advertencia, motivoNombreNuevo)));
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
