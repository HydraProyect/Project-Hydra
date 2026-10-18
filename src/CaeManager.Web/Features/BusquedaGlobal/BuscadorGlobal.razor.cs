using CaeManager.Application.BusquedaGlobal.Commands.RegistrarUsoReciente;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ContarVencenEstaSemana;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using Microsoft.AspNetCore.Components.Authorization;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.BusquedaGlobal.Recursos;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.BusquedaGlobal;

public partial class BuscadorGlobal
{
    private static readonly TimeSpan RetardoDebounce = TimeSpan.FromMilliseconds(250);

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcionAtajo;
    private IJSObjectReference? _suscripcionTab;
    private DotNetObjectReference<BuscadorGlobal>? _referenciaDotNet;
    private ElementReference _inputElemento;

    private bool _visible;
    private string _termino = string.Empty;

    /// <summary>
    /// Lo que se pinta en value="@_valorMostrado" del input — deliberadamente
    /// separado de _termino (que sí se actualiza en cada tecla para dirigir
    /// la búsqueda). Reflejar _termino directamente en el value del mismo
    /// input que lo genera es lo que permite que, bajo latencia, un render
    /// en cola de una pulsación anterior se aplique después de una más
    /// reciente y sobrescriba visualmente el campo con una versión más
    /// corta — el navegador ya tiene el valor correcto en su DOM, no hace
    /// falta devolvérselo en cada tecla. Solo se toca al abrir/cerrar el
    /// buscador (reinicio externo legítimo), igual que CampoTexto.
    /// </summary>
    private string _valorMostrado = string.Empty;

    private bool _buscando;
    private ResultadoBusquedaGlobalDto? _resultado;
    private int _indiceSeleccionado = -1;

    /// <summary>
    /// Mensaje de fallo de la búsqueda vigente. Existe porque un fallo no
    /// puede presentarse como un vacío: sin él, una excepción del handler
    /// dejaba <see cref="_resultado"/> en null con <see cref="_buscando"/>
    /// ya apagado, y el panel no pintaba absolutamente nada — el usuario
    /// leía "no hay nada" donde en realidad la búsqueda ni siquiera se
    /// completó.
    /// </summary>
    private string? _errorBusqueda;

    /// <summary>
    /// CancellationTokenSource del ciclo de vida del componente: su token
    /// viaja en TODAS las llamadas al mediador (búsqueda y recientes), y se
    /// cancela y libera una sola vez en <see cref="DisposeAsync"/>, que es el
    /// que Blazor llama de verdad aquí (el componente declara
    /// <c>IAsyncDisposable</c>, no <c>IDisposable</c>).
    ///
    /// Se pone a null ANTES de liberarlo para que cualquier lectura
    /// posterior vea null en vez de un CTS desechado: leer <c>.Token</c> de
    /// un CTS ya dispuesto lanza <see cref="ObjectDisposedException"/>, y en
    /// un continuation posterior a un await no habría nadie para recogerla.
    /// Por eso el token se captura siempre ANTES del primer await de cada
    /// operación, nunca después.
    /// </summary>
    private CancellationTokenSource? _cicloDeVida = new();

    /// <summary>CTS del debounce de la búsqueda en vuelo, enlazado al del ciclo de vida.</summary>
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Número de la búsqueda vigente. Se incrementa antes de cualquier await
    /// y se vuelve a comprobar después: una respuesta lenta de una búsqueda
    /// ya superada no puede pintarse sobre la vigente ni apagar su indicador
    /// de "Buscando…", aunque el handler haya ignorado el token de
    /// cancelación (cancelar es una petición, no una garantía).
    /// </summary>
    private int _generacionBusqueda;

    /// <summary>
    /// Número de la apertura vigente del palette. Cierra la misma carrera
    /// para la carga de "Recientes": si el usuario cierra y vuelve a abrir,
    /// los recientes que llegan tarde pertenecen a un contexto que ya no
    /// existe y no se pintan.
    /// </summary>
    private int _generacionApertura;

    /// <summary>
    /// Guarda de reentrada de <see cref="Seleccionar"/>. Hace falta aunque el
    /// palette se cierre en el mismo método: el segundo clic (o un Enter
    /// mientras el clic ya viajaba hacia el servidor) sale del navegador
    /// antes de que el cierre llegue al DOM, así que "ya no está visible" no
    /// impide una segunda ejecución. Solo se reabre en
    /// <see cref="AbrirAsync"/>, que es el cambio de contexto.
    /// </summary>
    private bool _navegando;

    /// <summary>"Recientes" del estado inicial — cargado una sola vez al abrir el palette, no en cada tecla.</summary>
    private IReadOnlyList<ItemRecienteDto> _recientes = [];

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// ¿El rol efectivo puede escribir? Lo mismo que pregunta <see cref="SoloConEscritura"/> ante el botón «Subir
    /// documento» de Trabajador 360: sin escritura, «Subir documento para {Trabajador}» no se ofrece. Se resuelve en
    /// cada apertura; el alta sigue autorizándose en servidor.
    /// </summary>
    private bool _puedeEscribir;

    /// <summary>Recuento «vencen esta semana» de cada Empresa sujeto de «Reclamar a…», pedido una vez por apertura; null = no calculado (fuera de cartera o fallo).</summary>
    private readonly Dictionary<Guid, int?> _vencenEstaSemana = [];

    /// <summary>"En esta pantalla" del estado inicial — resuelto una sola vez al abrir, a partir de la ruta actual.</summary>
    private IReadOnlyList<ItemBusquedaDto> _accionesPantalla = [];

    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    /// <summary>true mientras la query está vacía (&lt;2 caracteres) — el estado que muestra Recientes + En esta pantalla en vez de resultados de búsqueda.</summary>
    private bool ConsultaVacia => _termino.Trim().Length < 2;
    private DocumentoBuscado? DocumentoActual => DocumentoBuscado.Analizar(_termino);

    /// <summary>
    /// Marcador para "Guardar filtro actual" en <see cref="AccionesContextualesPorPantalla"/> — a
    /// diferencia de "Nuevo X"/"Exportar a Excel" (rutas fijas), esta acción
    /// tiene que preservar los filtros que ya haya en la URL actual (?q=,
    /// ?estado=...), así que su ruta real se construye en <see cref="ConstruirAccionesPantalla"/>
    /// con <c>NavigationManager.GetUriWithQueryParameter</c> en vez de ser un literal aquí.
    /// </summary>
    private const string MarcadorGuardarFiltro = "__guardar-filtro__";

    /// <summary>
    /// Acciones contextuales por pantalla del grupo "En esta pantalla" —
    /// mismo estilo literal que <see cref="CoberturaDePaleta.DestinosNavegacion"/>/<see cref="AccionesFijas"/>.
    /// Solo cubre verbos que YA existen de verdad en cada pantalla (ver plan
    /// de implementación): no se inventa "Exportar la vista" ni "Guardar
    /// filtro" donde el propio módulo no lo tiene todavía.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Titulo, string Ruta)>> AccionesContextualesPorPantalla =
        new Dictionary<string, IReadOnlyList<(string, string)>>
        {
            ["clientes"] = [("Nuevo Cliente", "/clientes?accion=crear"), ("Exportar a Excel", "/clientes/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["empresas"] = [("Nueva empresa", "/empresas?accion=crear"), ("Exportar a Excel", "/empresas/exportar.xlsx")],
            ["subcontratas"] = [("Nueva subcontrata", "/subcontratas?accion=crear"), ("Exportar a Excel", "/subcontratas/exportar.xlsx")],
            ["centros"] = [("Nuevo centro", "/centros?accion=crear"), ("Exportar a Excel", "/centros/exportar.xlsx")],
            ["trabajadores"] = [("Nuevo trabajador", "/trabajadores?accion=crear"), ("Exportar a Excel", "/trabajadores/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["documentos"] = [("Nuevo documento", "/documentos?accion=crear"), ("Exportar a Excel", "/documentos/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
        };

    /// <summary>Grupo "Acciones" del palette — verbos que crean algo, nunca navegación pura.</summary>
    private static readonly IReadOnlyList<(string Nombre, string Ruta)> AccionesFijas =
    [
        ("Crear Cliente", "/clientes?accion=crear"),
        ("Alta guiada de Cliente", "/clientes/alta-guiada"),
        ("Crear documento", "/documentos?accion=crear"),
        ("Reclamar…", "/comunicaciones"),
        ("Subir…", "/documentos?accion=crear"),
    ];

    private IReadOnlyList<ItemBusquedaDto> IrA
    {
        get
        {
            var termino = _termino.Trim();
            if (termino.StartsWith("ir a", StringComparison.OrdinalIgnoreCase))
                termino = termino[4..].Trim();
            if (_termino.Trim().Equals("ir a", StringComparison.OrdinalIgnoreCase))
                return CoberturaDePaleta.DestinosNavegacion.Select(c => new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta)).ToList();
            if (termino.Length < 2) return [];

            return CoberturaDePaleta.DestinosNavegacion
                .Where(c => c.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta))
                .ToList();
        }
    }

    /// <summary>
    /// Acciones fijas (filtradas por coincidencia de nombre) más, cuando la
    /// búsqueda de una categoría no encontró nada, un atajo para crearla con
    /// el término ya escrito precargado como nombre — pedido explícito: "si
    /// introduce una empresa/centro/trabajador que no se encuentre, dar la
    /// opción de crearlo con el nombre ya prellenado".
    ///
    /// Si el término empieza por un verbo de la maqueta (reclamar, subir, ir a)
    /// las acciones llevan sujeto —«Reclamar a {Empresa}», «Subir documento para
    /// {Trabajador}», «Ir a {Centro}»— tomado de los Recientes de la apertura
    /// (<see cref="AccionesConSujeto"/>); sin sujeto disponible queda la acción
    /// genérica. Con un verbo escrito nunca se ofrece «Crear X «término»»: el
    /// término no es un nombre.
    /// </summary>
    private IReadOnlyList<ItemPaleta> Acciones
    {
        get
        {
            var termino = _termino.Trim();
            if (termino.Length < 2) return [];
            if (DocumentoActual is not null) return [];

            var verbo = VerboDeAccion(termino);
            var conSujeto = verbo is null ? [] : AccionesConSujeto(FiltroTrasElVerbo(termino, verbo));
            var esIrA = verbo == VerboIrA;
            var esperadas = conSujeto.Select(a => a.PistaEnter).ToHashSet();

            var acciones = esIrA
                ? []
                : AccionesFijas
                    .Where(c => c.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase))
                    // Una acción con sujeto sustituye a su versión genérica.
                    .Where(c => !(c.Nombre == "Reclamar…" && esperadas.Contains("Reclamar")) &&
                                !(c.Nombre == "Subir…" && esperadas.Contains("Subir")))
                    .Select(c => ComoAccion(new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta)))
                    .ToList();

            acciones.InsertRange(0, conSujeto);

            if (_resultado is not null && verbo is null && !acciones.Any(a => a.Item.Titulo is "Reclamar…" or "Subir…"))
            {
                var terminoCodificado = Uri.EscapeDataString(termino);

                if (_resultado.Empresas.Count == 0)
                    acciones.Add(ComoAccion(new ItemBusquedaDto(Guid.Empty, $"Crear empresa «{termino}»", null, $"/empresas?accion=crear&nombre={terminoCodificado}")));
                if (_resultado.Centros.Count == 0)
                    acciones.Add(ComoAccion(new ItemBusquedaDto(Guid.Empty, $"Crear centro «{termino}»", null, $"/centros?accion=crear&nombre={terminoCodificado}")));
                if (_resultado.Trabajadores.Count == 0)
                    acciones.Add(ComoAccion(new ItemBusquedaDto(Guid.Empty, $"Crear trabajador «{termino}»", null, $"/trabajadores?accion=crear&nombre={terminoCodificado}")));
            }

            return acciones;
        }
    }

    private static string Texto(string clave) => TextosBusquedaGlobal.Texto(clave);

    private static string Texto(string clave, string argumento) =>
        string.Format(CultureInfo.CurrentUICulture, TextosBusquedaGlobal.Texto(clave), argumento);

    private static string Placeholder => TextosBusquedaGlobal.Texto("Placeholder");

    private const string VerboReclamar = "reclamar";
    private const string VerboSubir = "subir";
    private const string VerboIrA = "ir a";

    /// <summary>El verbo de la maqueta con el que empieza el término como palabra completa (sin distinguir mayúsculas), o null: «Subirats» no es el verbo «subir».</summary>
    private static string? VerboDeAccion(string termino) =>
        new[] { VerboReclamar, VerboSubir, VerboIrA }
            .FirstOrDefault(v => termino.StartsWith(v, StringComparison.OrdinalIgnoreCase)
                && (termino.Length == v.Length || termino[v.Length] == ' '));

    /// <summary>Lo escrito tras el verbo y su preposición («reclamar a Refri» → «Refri»); vacío si solo hay verbo.</summary>
    private static string FiltroTrasElVerbo(string termino, string verbo)
    {
        var resto = termino[verbo.Length..].Trim();
        foreach (var preposicion in new[] { "para", "a" })
        {
            if (resto.Equals(preposicion, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (resto.StartsWith(preposicion + " ", StringComparison.OrdinalIgnoreCase))
                return resto[(preposicion.Length + 1)..].Trim();
        }
        return resto;
    }

    /// <summary>
    /// Acciones con sujeto de la maqueta, en su orden. El sujeto sale de los
    /// Recientes ya cargados al abrir —el palette no hace ninguna consulta
    /// nueva, así que el alcance de cartera no se ensancha—: la Empresa, el
    /// Trabajador y el Centro más recientes que casen con lo escrito tras el
    /// verbo. Sin reciente de ese tipo no hay acción con sujeto y queda la
    /// genérica. El subtítulo de «Reclamar a {Empresa}» es «N documentos vencen
    /// esta semana» (<see cref="ContarVencenEstaSemanaDeEmpresaQuery"/>, pedido
    /// una sola vez para ese sujeto en <see cref="CargarVencenEstaSemanaAsync"/>);
    /// sin recuento, va vacío. «Reclamar» abre Comunicaciones sin fijar la
    /// Empresa: la Bandeja solo admite el filtro por Cliente empresarial.
    /// «Subir documento para…» solo se ofrece con escritura.
    /// </summary>
    private List<ItemPaleta> AccionesConSujeto(string filtro)
    {
        bool Casa(ItemRecienteDto r) =>
            filtro.Length == 0 || r.Titulo.Contains(filtro, StringComparison.OrdinalIgnoreCase);

        var acciones = new List<ItemPaleta>();

        if (EmpresaSujeto(filtro) is { } empresa)
            acciones.Add(new ItemPaleta(
                new ItemBusquedaDto(Guid.Empty, Texto("ReclamarA", empresa.Titulo), null, "/comunicaciones"),
                null, "enviar", TextoVencenEstaSemana(empresa.EntidadId), "Reclamar"));

        if (_puedeEscribir && _recientes.FirstOrDefault(r => r.Tipo == "Trabajador" && r.EntidadId is not null && Casa(r)) is { } trabajador)
            acciones.Add(new ItemPaleta(
                new ItemBusquedaDto(Guid.Empty, Texto("SubirDocumentoPara", trabajador.Titulo), null, RutaSubirDocumentoDeTrabajador(trabajador.EntidadId!.Value)),
                null, "importar", string.IsNullOrWhiteSpace(trabajador.Subtitulo) ? string.Empty : trabajador.Subtitulo, "Subir"));

        if (_recientes.FirstOrDefault(r => r.Tipo == "Centro" && EsRutaLocal(r.UrlDestino) && Casa(r)) is { } centro)
            acciones.Add(new ItemPaleta(
                new ItemBusquedaDto(Guid.Empty, $"Ir a {centro.Titulo}", null, centro.UrlDestino),
                null, "centros", string.Empty, "Abrir"));

        return acciones;
    }

    /// <summary>La Empresa reciente que casa con lo escrito tras el verbo: el sujeto de «Reclamar a…».</summary>
    private ItemRecienteDto? EmpresaSujeto(string filtro) =>
        _recientes.FirstOrDefault(r => r.Tipo == "Empresa" &&
            (filtro.Length == 0 || r.Titulo.Contains(filtro, StringComparison.OrdinalIgnoreCase)));

    private string TextoVencenEstaSemana(Guid? empresaId) =>
        empresaId is { } id && _vencenEstaSemana.TryGetValue(id, out var recuento) && recuento is { } n
            ? n == 1 ? Texto("VencenEstaSemanaUno") : Texto("VencenEstaSemanaVarios", n.ToString(CultureInfo.CurrentCulture))
            : string.Empty;

    /// <summary>
    /// Pide el recuento «vencen esta semana» del sujeto de «Reclamar a…» si el término lleva verbo y aún no se pidió en
    /// esta apertura. Una consulta por sujeto, nunca por tecla; si falla, la acción sigue ofreciéndose sin subtítulo.
    /// </summary>
    private async Task CargarVencenEstaSemanaAsync(string termino, CancellationToken token)
    {
        var limpio = termino.Trim();
        if (DocumentoBuscado.Analizar(limpio) is not null || VerboDeAccion(limpio) is not { } verbo || verbo == VerboIrA) return;
        if (EmpresaSujeto(FiltroTrasElVerbo(limpio, verbo)) is not { EntidadId: { } empresaId } ||
            _vencenEstaSemana.ContainsKey(empresaId)) return;

        try
        {
            _vencenEstaSemana[empresaId] = await Mediator.Send(new ContarVencenEstaSemanaDeEmpresaQuery(empresaId), token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _vencenEstaSemana[empresaId] = null;
        }
    }

    /// <summary>Texto del estado del Trabajador en una búsqueda por DNI/NIE: el de la maqueta («Al día», «2 documentos vencidos», «Pendiente») sobre el estado del listado de Trabajadores.</summary>
    internal static string TextoEstadoTrabajador(EstadoTrabajadorBuscadoDto estado)
    {
        if (estado.DocumentosVencidos > 0)
            return estado.DocumentosVencidos == 1
                ? Texto("EstadoVencidosUno")
                : Texto("EstadoVencidosVarios", estado.DocumentosVencidos.ToString(CultureInfo.CurrentCulture));

        return EstadoDocumentoUi.ConFaltantes(estado.PeorEstado, estado.HayFaltantes) switch
        {
            null => EstadoDocumentoUi.TextoDocumental(null),
            EstadoDocumento.Faltante => Texto("EstadoPendiente"),
            { } peor when EstadoDocumentoUi.EsCorrecto(peor) => Texto("EstadoAlDia"),
            { } peor => EstadoDocumentoUi.Texto(peor),
        };
    }

    /// <summary>
    /// Trabajador 360 en la pestaña Documentación con <c>accion=subir</c>: la ficha abre allí la subida con el
    /// Trabajador fijado, solo si el rol puede escribir (la misma comprobación que el botón «Subir documento»).
    /// </summary>
    private static string RutaSubirDocumentoDeTrabajador(Guid trabajadorId) =>
        $"/trabajadores/{trabajadorId}?pestana=documentacion&accion=subir";

    private static bool EsRutaLocal(string url) =>
        url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\');

    /// <summary>
    /// Una fila del palette, ya resuelta para pintar: el DTO, el tipo con el
    /// que se registra su uso (null = "Ir a", navegación pura que nunca
    /// cuenta como uso), el icono, el subtítulo mostrado y la pista de Enter
    /// del mockup Gen 2 ("↵ abrir ficha" / "↵ ir" / "↵ ejecutar").
    /// </summary>
    private sealed record ItemPaleta(ItemBusquedaDto Item, string? TipoHistorial, string Icono, string Subtitulo, string PistaEnter);

    /// <summary>Una sección del palette, con el título en versalitas del mockup.</summary>
    private sealed record GrupoPaleta(string Titulo, IReadOnlyList<ItemPaleta> Items, string? UrlVerMas = null);

    /// <summary>
    /// Etiqueta de tipo de cada categoría de entidad, en terminología
    /// canónica. El subtítulo que devuelve BuscarGlobalQueryHandler para
    /// Centro es el literal del tipo, así que mostrarlo tal cual repetiría
    /// la etiqueta. Para Trabajador (razón social de su organización
    /// empleadora, nunca el DNI: decisión del 2026-09-24) y Documento
    /// (propietario) el subtítulo del DTO sí aporta información distinta y se
    /// concatena.
    /// "Cliente"/"Empresa"/"Subcontrata" ya no los emite el handler para
    /// resultados directos —Empresa tiene su propia composición, ver
    /// <see cref="ComponerSubtituloEmpresa"/>— pero siguen aquí porque
    /// <see cref="SubtituloDeReciente"/> los necesita para leer recientes
    /// registrados antes de P41d (2026-09-18), que guardaron esos literales
    /// tal cual.
    /// </summary>
    private static readonly HashSet<string> EtiquetasDeTipoDelHandler =
        new(StringComparer.OrdinalIgnoreCase) { "Cliente", "Empresa", "Subcontrata", "Centro" };

    private static string ComponerSubtitulo(string etiquetaTipo, string? subtituloDto) =>
        string.IsNullOrWhiteSpace(subtituloDto) || EtiquetasDeTipoDelHandler.Contains(subtituloDto.Trim())
            ? etiquetaTipo
            : $"{etiquetaTipo} · {subtituloDto}";

    /// <summary>
    /// Traducción de cada papel en bruto que emite el handler a su etiqueta
    /// canónica del contrato de lenguaje — la contraparte de una Relación
    /// Empresarial es el Cliente empresarial, que en pantalla se rotula «Cliente».
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EtiquetaPorPapel =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cliente"] = "Cliente",
            ["Subcontrata"] = "Subcontrata",
        };

    /// <summary>
    /// Subtítulo de una fila de Empresa: <paramref name="papelesCsv"/> es el
    /// micro-formato del handler ("Cliente,Subcontrata", o vacío si la
    /// Empresa no tiene ningún papel contextual) — P41d (2026-09-18): una
    /// Empresa aparece una sola vez en el buscador aunque tenga varios
    /// papeles, y sus papeles se muestran aquí como dato secundario en vez
    /// de como categorías separadas.
    /// </summary>
    private static string ComponerSubtituloEmpresa(string? papelesCsv)
    {
        if (string.IsNullOrWhiteSpace(papelesCsv)) return "Empresa";

        var etiquetas = papelesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => EtiquetaPorPapel.GetValueOrDefault(p, p));

        return string.Join(" · ", etiquetas);
    }

    /// <summary>
    /// Los grupos tal y como se pintan, en orden. Es la ÚNICA fuente del
    /// orden: <see cref="ElementosPlanos"/> y <see cref="InicioDeGrupo"/> se
    /// derivan de aquí, así que el índice que resalta el render y el que
    /// mueve ↑↓/Tab/Enter no pueden desincronizarse — antes eran tres listas
    /// escritas a mano en paralelo y cualquier categoría añadida a una y
    /// olvidada en otra habría hecho que Enter abriera un elemento distinto
    /// del resaltado.
    /// </summary>
    private IReadOnlyList<GrupoPaleta> GruposVisibles
    {
        get
        {
            var grupos = new List<GrupoPaleta>();
            if (DocumentoActual is { EsValido: false }) return grupos;

            if (ConsultaVacia)
            {
                if (_recientes.Count > 0)
                {
                    grupos.Add(new GrupoPaleta("Recientes", [.. _recientes.Select(r => new ItemPaleta(
                        RecienteComoItem(r),
                        r.Tipo,
                        IconoParaTipo(r.Tipo),
                        SubtituloDeReciente(r),
                        r.Tipo == "Accion" ? "ejecutar" : "abrir ficha"))]));
                }

                if (_accionesPantalla.Count > 0)
                    grupos.Add(new GrupoPaleta("En esta pantalla", [.. _accionesPantalla.Select(ComoAccion)]));

                return grupos;
            }

            // Búsqueda por documento completo y válido: la maqueta no usa el grupo
            // genérico «Entidades» sino «Trabajador» (+ «Acciones») o «Empresa».
            if (DocumentoActual is { EsValido: true } documento)
                return GruposPorDocumento(documento);

            if (_resultado is not null && _resultado.TieneResultados)
            {
                List<ItemPaleta> entidades =
                [
                    .. ComoEntidadesEmpresa(_resultado.Empresas),
                    .. ComoEntidades(_resultado.Centros, "Centro", "Centro", "centros"),
                    .. ComoEntidades(_resultado.Trabajadores, "Trabajador", "Trabajador", "trabajadores"),
                    .. ComoEntidades(_resultado.Documentos, "Documento", "Documento", "documentos"),
                ];

                grupos.Add(new GrupoPaleta("Entidades", entidades, UrlVerMas(_resultado, _termino.Trim())));
            }

            if (IrA.Count > 0)
            {
                grupos.Add(new GrupoPaleta("Ir a", [.. IrA.Select(i =>
                    new ItemPaleta(i, null, "configuracion", string.Empty, "ir"))]));
            }

            var acciones = Acciones;
            if (acciones.Count > 0)
                grupos.Add(new GrupoPaleta("Acciones", acciones));

            return grupos;
        }
    }

    /// <summary>
    /// «Ver más →» del grupo Entidades: el listado del primer módulo (en el orden del grupo) que tiene más
    /// coincidencias de las que caben, con el término en su filtro (?q=). El listado aplica su propio alcance; el
    /// palette no consulta nada más. Null si ninguna categoría se quedó corta.
    /// </summary>
    private static string? UrlVerMas(ResultadoBusquedaGlobalDto resultado, string termino)
    {
        // Empresas: el listado lo decide la consulta con el mismo criterio que cada fila (clientes, subcontratas o
        // empresas según el alcance que la trajo), para no aterrizar en un listado donde no se ven.
        var modulo = resultado.ListadoVerMasEmpresas
            ?? (resultado.HayMasCentros ? "centros"
                : resultado.HayMasTrabajadores ? "trabajadores"
                : resultado.HayMasDocumentos ? "documentos"
                : null);
        return modulo is null ? null : $"/{modulo}?q={Uri.EscapeDataString(termino)}";
    }

    /// <summary>
    /// «Buscar en documentos por identificador →» del bloque «Sin resultados»: Documentos con lo escrito en su
    /// filtro, salvo un DNI/NIE, que nunca viaja en la URL (historial, registros, Referer): entonces Documentos sin
    /// filtro.
    /// </summary>
    private string UrlBuscarEnDocumentos =>
        DocumentoActual is { Tipo: CaeManager.Domain.Common.TipoIdentificacion.Dni or CaeManager.Domain.Common.TipoIdentificacion.Nie }
            ? "/documentos"
            : $"/documentos?q={Uri.EscapeDataString(_termino.Trim())}";

    /// <summary>Enlace fuera de la lista navegable («Ver más →», «Buscar en documentos…»): cierra y navega, sin registrar uso.</summary>
    private void NavegarFueraDeLista(string url)
    {
        if (_navegando) return;
        _navegando = true;
        Cerrar();
        Navigation.NavigateTo(url);
    }

    private static ItemPaleta ComoAccion(ItemBusquedaDto item) =>
        new(item, "Accion", "editar", string.Empty, "ejecutar");

    private IEnumerable<ItemPaleta> ComoEntidades(
        IReadOnlyList<ItemBusquedaDto> items, string tipoHistorial, string etiquetaTipo, string icono) =>
        items.Select(i => new ItemPaleta(i, tipoHistorial, icono,
            ComponerSubtitulo(etiquetaTipo, i.Subtitulo), "abrir ficha"));

    private IEnumerable<ItemPaleta> ComoEntidadesEmpresa(IReadOnlyList<ItemBusquedaDto> items) =>
        items.Select(i => new ItemPaleta(i, "Empresa", "empresas",
            ComponerSubtituloEmpresa(i.Subtitulo), "abrir ficha"));

    /// <summary>
    /// Resultado de buscar un DNI/NIE o un CIF completo y válido, tal y como lo
    /// dibuja la maqueta del Command Palette. Solo ve lo que devolvió la
    /// consulta (alcance de cartera y RLS ya aplicados); el documento buscado
    /// no se proyecta en ninguna fila.
    /// </summary>
    private IReadOnlyList<GrupoPaleta> GruposPorDocumento(DocumentoBuscado documento)
    {
        var grupos = new List<GrupoPaleta>();
        if (_resultado is null) return grupos;

        if (documento.Tipo == CaeManager.Domain.Common.TipoIdentificacion.NifEmpresa)
        {
            if (_resultado.Empresas.Count > 0)
                grupos.Add(new GrupoPaleta("Empresa", [.. _resultado.Empresas.Select(e => new ItemPaleta(
                    e with { UrlDestino = Ficha360DeEmpresa(e) }, "Empresa", "empresas",
                    Texto("CoincideCif"), Texto("AbrirEmpresa360")))]));
            return grupos;
        }

        if (_resultado.Trabajadores.Count == 0) return grupos;

        // «Coincide con el documento buscado · {empresa} · {estado}», como en la maqueta; sin estado (no calculado), sin él.
        grupos.Add(new GrupoPaleta("Trabajador", [.. _resultado.Trabajadores.Select(t => new ItemPaleta(
            t, "Trabajador", "trabajadores",
            t.EstadoTrabajador is { } estado
                ? $"{ComponerSubtitulo(Texto("CoincideDocumento"), t.Subtitulo)} · {TextoEstadoTrabajador(estado)}"
                : ComponerSubtitulo(Texto("CoincideDocumento"), t.Subtitulo),
            Texto("AbrirTrabajador360")))]));

        // Las acciones son sobre la primera coincidencia, como en la maqueta.
        var primero = _resultado.Trabajadores[0];
        List<ItemPaleta> acciones = [];
        if (_puedeEscribir)
            acciones.Add(new(new ItemBusquedaDto(Guid.Empty, Texto("SubirDocumentoPara", primero.Titulo), null, RutaSubirDocumentoDeTrabajador(primero.Id)),
                null, "importar", Texto("SubirDocumentoParaSub"), "Subir"));
        // La organización empleadora es el subtítulo del Trabajador; sin ella no hay a quién reclamar.
        if (!string.IsNullOrWhiteSpace(primero.Subtitulo))
            acciones.Add(new(new ItemBusquedaDto(Guid.Empty, Texto("ReclamarA", primero.Subtitulo), null, "/comunicaciones"),
                null, "enviar", Texto("ReclamarASub", primero.Titulo), "Reclamar"));
        if (acciones.Count > 0)
            grupos.Add(new GrupoPaleta("Acciones", acciones));

        return grupos;
    }

    /// <summary>
    /// Empresa 360 de una fila de Empresa. El papel que ya decidió el listado de
    /// la búsqueda por nombre decide también la ficha, con la misma precedencia
    /// que el handler (Cliente, luego Subcontrata, luego Empresa sin papel): es la
    /// ficha cuyo alcance coincide con la consulta que trajo la fila.
    /// </summary>
    private static string Ficha360DeEmpresa(ItemBusquedaDto empresa)
    {
        var papeles = (empresa.Subtitulo ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return papeles.Contains("Cliente", StringComparer.OrdinalIgnoreCase) ? $"/clientes/{empresa.Id}"
            : papeles.Contains("Subcontrata", StringComparer.OrdinalIgnoreCase) ? $"/subcontratas/{empresa.Id}"
            : $"/empresas/{empresa.Id}";
    }

    /// <summary>Todas las filas en el orden en que se pintan, para navegar con ↑↓/Tab + Enter.</summary>
    private IReadOnlyList<ItemPaleta> ElementosPlanos => [.. GruposVisibles.SelectMany(g => g.Items)];

    /// <summary>Índice (en <see cref="ElementosPlanos"/>) de la primera fila de cada grupo, en orden — usado por Tab para saltar de grupo en vez de fila a fila.</summary>
    private IReadOnlyList<int> InicioDeGrupo
    {
        get
        {
            var inicios = new List<int>();
            var acumulado = 0;

            foreach (var grupo in GruposVisibles)
            {
                if (grupo.Items.Count > 0) inicios.Add(acumulado);
                acumulado += grupo.Items.Count;
            }

            return inicios;
        }
    }

    /// <summary>
    /// Un reciente guardado lleva el subtítulo tal y como lo emitió el
    /// handler en el momento de registrarse. Para "Empresa" ese literal es
    /// el micro-formato en bruto de papeles (<see cref="ComponerSubtituloEmpresa"/>)
    /// — P41d, 2026-09-18 — y para el resto de tipos, el literal del tipo que
    /// neutraliza <see cref="ComponerSubtitulo"/>. "Cliente"/"Subcontrata"
    /// siguen resolviéndose aquí para los recientes registrados ANTES de
    /// P41d, cuando el buscador todavía los trataba como categorías propias
    /// — nunca se reescriben, así que sus filas ya guardadas deben seguir
    /// leyéndose igual que siempre.
    /// </summary>
    private static string SubtituloDeReciente(ItemRecienteDto r) =>
        r.Tipo == "Empresa"
            ? ComponerSubtituloEmpresa(r.Subtitulo)
            : EtiquetaVisibleDeTipo(r.Tipo) is { } etiqueta
                ? ComponerSubtitulo(etiqueta, r.Subtitulo)
                : r.Subtitulo ?? string.Empty;

    /// <summary>
    /// Etiqueta canónica de cada tipo de entidad; null para los que no son
    /// entidad (una acción no lleva etiqueta de tipo). "Cliente"/"Subcontrata"
    /// ya no los emite el handler (P41d, 2026-09-18) pero siguen aquí para
    /// leer recientes registrados antes de ese cambio.
    /// </summary>
    private static string? EtiquetaVisibleDeTipo(string tipo) => tipo switch
    {
        "Cliente" => "Cliente",
        "Empresa" => "Empresa",
        "Subcontrata" => "Subcontrata",
        "Centro" => "Centro",
        "Trabajador" => "Trabajador",
        "Documento" => "Documento",
        _ => null
    };

    private static ItemBusquedaDto RecienteComoItem(ItemRecienteDto r) =>
        new(r.EntidadId ?? Guid.Empty, r.Titulo, r.Subtitulo, r.UrlDestino);

    /// <summary>Icono por tipo de "reciente" — mismo Nombre que ya usan las categorías de Entidades; "Accion" reutiliza el icono del grupo Acciones. Cualquier tipo sin icono propio cae en "resultado" (registro genérico), nunca en "editar", que significa acción.</summary>
    private static string IconoParaTipo(string tipo) => tipo switch
    {
        "Cliente" => "clientes",
        "Empresa" => "empresas",
        "Subcontrata" => "subcontratas",
        "Centro" => "centros",
        "Trabajador" => "trabajadores",
        "Documento" => "documentos",
        "Accion" => "editar",
        _ => "resultado"
    };

    /// <summary>
    /// "En esta pantalla" del estado inicial — resuelve la ruta actual contra
    /// <see cref="AccionesContextualesPorPantalla"/>. "Guardar filtro actual"
    /// se construye añadiendo el parámetro a la URL ACTUAL completa (con los
    /// filtros que ya tenga), nunca sustituyéndola por una plantilla fija:
    /// si se perdieran los demás parámetros (?q=, ?estado=...) al navegar,
    /// la página resincronizaría sus filtros desde una URL vacía antes de
    /// abrir el modal, y "guardar filtro actual" acabaría guardando un
    /// filtro vacío.
    /// </summary>
    private IReadOnlyList<ItemBusquedaDto> ConstruirAccionesPantalla(bool exportarPermitido = false)
    {
        var segmento = SegmentoDeRuta(Navigation.Uri);
        if (segmento is null || !AccionesContextualesPorPantalla.TryGetValue(segmento, out var acciones))
            return [];

        return acciones
            // La exportación de Trabajadores solo se ofrece cuando ya se ha comprobado que la
            // pantalla no está en el estado 4a (ver ResolverExportacionDeTrabajadoresAsync).
            .Where(a => exportarPermitido || !EsExportacionDeTrabajadores(a.Ruta))
            .Select(a => new ItemBusquedaDto(
                Guid.Empty,
                a.Titulo,
                null,
                a.Ruta == MarcadorGuardarFiltro
                    ? Navigation.GetUriWithQueryParameter("accion", "guardar-filtro")
                    : a.Ruta))
            .ToList();
    }

    private static bool EsExportacionDeTrabajadores(string ruta) =>
        string.Equals(ruta, RutaExportarTrabajadores, StringComparison.Ordinal);

    private const string RutaExportarTrabajadores = "/trabajadores/exportar.xlsx";

    /// <summary>
    /// Estado 4a (misma condición que aplica la página vía <c>ContextoEmpresaActiva</c>; el endpoint y esta
    /// paleta usan <see cref="ClientesAutorizados.PideElegirEmpresa"/>): solo si la comprobación sale
    /// limpia se añade la exportación. Cualquier fallo la deja fuera.
    /// </summary>
    private async Task ResolverExportacionDeTrabajadoresAsync(int apertura, CancellationToken token)
    {
        // D-14: el ámbito honesto de la búsqueda (la empresa gestionada activa, no «tu cartera»)
        // sale de la misma lista autorizada que decide el estado 4a: una sola consulta por apertura.
        // Es solo un rótulo: el alcance real lo siguen decidiendo la consulta y RLS.
        var enTrabajadores = SegmentoDeRuta(Navigation.Uri) == "trabajadores";
        _nombreAmbito = null;
        _selectorVisible = false;
        try
        {
            var autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery(), token);
            if (apertura != _generacionApertura) return;
            var activo = ClientesAutorizados.Activo(autorizados, TenantActual.TenantId);
            // Estado 4a: sin empresa elegida no hay empresa activa que nombrar (el Tenant de origen no es
            // una empresa de la cartera): rótulo genérico, igual que pide la pantalla.
            _nombreAmbito = ClientesAutorizados.PideElegirEmpresa(autorizados, TenantActual.TenantId) ? null : activo?.Nombre;
            _selectorVisible = ClientesAutorizados.SelectorVisible(autorizados, activo);
            if (enTrabajadores && !ClientesAutorizados.PideElegirEmpresa(autorizados, TenantActual.TenantId))
                _accionesPantalla = ConstruirAccionesPantalla(exportarPermitido: true);
            StateHasChanged();
        }
        catch
        {
            // Sin comprobación no se ofrece la exportación y el rótulo queda genérico.
        }
    }

    /// <summary>Nombre de la empresa gestionada activa, resuelto al abrir; null si aún no se conoce.</summary>
    private string? _nombreAmbito;

    /// <summary>El selector de empresa existe solo con varios Tenants: sin él no se aconseja «cámbiala en el selector».</summary>
    private bool _selectorVisible;

    private string AmbitoRotulo => string.IsNullOrWhiteSpace(_nombreAmbito)
        ? TextosBusquedaGlobal.Texto("AmbitoGenerico")
        : _nombreAmbito;

    private string TextoAmbitoPie => string.Format(CultureInfo.CurrentUICulture, TextosBusquedaGlobal.Texto("AmbitoPie"), AmbitoRotulo);

    private string TextoSinResultados =>
        DocumentoActual is { } documento
        ? documento.Tipo == CaeManager.Domain.Common.TipoIdentificacion.NifEmpresa
            ? Texto("SinResultadosCif")
            : Texto("SinResultadosTrabajadorDocumento")
        :
        string.Format(CultureInfo.CurrentUICulture, TextosBusquedaGlobal.Texto("SinResultados"), _termino, AmbitoRotulo)
        + (_selectorVisible ? " " + TextosBusquedaGlobal.Texto("SinResultadosPistaSelector") : "");

    /// <summary>
    /// Segmento de ruta, pero SOLO si es la pantalla de listado exacta
    /// (<c>/trabajadores</c>), nunca una sub-ruta (<c>/trabajadores/{id}</c>
    /// — Trabajador 360, Centro 360). "Guardar filtro actual" navegaría a
    /// esa misma sub-ruta con <c>?accion=guardar-filtro</c> añadido, y esa
    /// página de ficha no tiene ningún código que reaccione a ese parámetro
    /// (vive en la página de listado) — mostrarlo ahí sería una acción que
    /// no hace nada. Sin match exacto, "En esta pantalla" no muestra nada,
    /// que es el comportamiento correcto para una pantalla sin acciones
    /// contextuales definidas.
    /// </summary>
    private static string? SegmentoDeRuta(string uri)
    {
        var segmentos = new Uri(uri).AbsolutePath.Trim('/').Split('/');
        return segmentos.Length == 1 && !string.IsNullOrEmpty(segmentos[0]) ? segmentos[0] : null;
    }

    protected override void OnInitialized()
    {
        BusquedaGlobalService.SolicitudAbrir += AbrirDesdeServicio;

        // La navegación mejorada de Blazor reutiliza esta instancia entre
        // páginas (no se recrea el Layout en cada navegación) — sin esto, al
        // hacer clic en un resultado la superposición se quedaba abierta
        // tapando la página de destino.
        Navigation.LocationChanged += ManejarCambioDeUbicacion;
    }

    private void ManejarCambioDeUbicacion(object? sender, LocationChangedEventArgs e)
    {
        if (!_visible) return;

        // LocationChanged no es un evento de Blazor (no dispara StateHasChanged solo) —
        // hay que marshalear al dispatcher del circuito explícitamente.
        _ = InvokeAsync(() =>
        {
            Cerrar();
            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/buscador-global.js");
        _referenciaDotNet = DotNetObjectReference.Create(this);
        _suscripcionAtajo = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajoBuscador", _referenciaDotNet);
    }

    private void AbrirDesdeServicio() => _ = AbrirAsync();

    [JSInvokable]
    public Task AbrirDesdeJs() => AbrirAsync();

    private async Task AbrirAsync()
    {
        // Nueva apertura = nuevo contexto: nada de lo que quedó preparado
        // para la anterior puede arrastrarse. _recientes se vacía aquí en vez
        // de dejarse hasta que llegue la carga nueva, porque si no el palette
        // reabierto pintaba durante ese hueco los recientes del contexto
        // anterior y una pulsación de Enter los habría abierto.
        var apertura = ++_generacionApertura;
        ++_generacionBusqueda;

        _visible = true;
        _termino = string.Empty;
        _valorMostrado = string.Empty;
        _resultado = null;
        _errorBusqueda = null;
        _buscando = false;
        _indiceSeleccionado = -1;
        _navegando = false;
        _recientes = [];
        _vencenEstaSemana.Clear();

        // Una sola vez por apertura, no en cada tecla (a diferencia de la
        // búsqueda, que sí se repite con el debounce).
        _accionesPantalla = ConstruirAccionesPantalla();

        StateHasChanged();

        // El token se captura ANTES de cualquier await de este método.
        var token = TokenDeCicloDeVida();
        if (token is null) return;

        // El módulo se captura en una local: entre esta comprobación y su
        // uso hay un await, y DisposeAsync puede haberlo liberado y puesto a
        // null en ese hueco. Tras reanudar se vuelve a mirar el token, que es
        // la señal de que el componente se retiró.
        var modulo = _modulo;
        if (modulo is not null)
        {
            // Espera al siguiente render para que el <input> ya esté en el DOM antes de enfocarlo.
            await Task.Yield();

            if (token.Value.IsCancellationRequested) return;

            await modulo.InvokeVoidAsync("enfocarElemento", _inputElemento);

            // Se re-registra en cada apertura: el <input> es un elemento del
            // DOM nuevo cada vez (vive dentro del @if (_visible)), así que el
            // listener de la apertura anterior ya se perdió con él.
            var suscripcionAnterior = _suscripcionTab;
            _suscripcionTab = null;
            if (suscripcionAnterior is not null)
                await suscripcionAnterior.DisposeAsync();

            if (token.Value.IsCancellationRequested) return;

            _suscripcionTab = await modulo.InvokeAsync<IJSObjectReference>("registrarSaltoDeGrupo", _inputElemento, _referenciaDotNet);
        }

        await ResolverExportacionDeTrabajadoresAsync(apertura, token.Value);
        if (apertura != _generacionApertura) return;

        _puedeEscribir = await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion);
        if (apertura != _generacionApertura) return;

        try
        {
            var recientes = await Mediator.Send(new ObtenerRecientesQuery(), token.Value);

            // Los recientes de una apertura ya superada pertenecen a un
            // contexto que ya no existe: llegan tarde y no se pintan.
            if (apertura != _generacionApertura) return;

            _recientes = recientes;
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // El componente se destruyó o el palette se cerró mientras cargaba.
        }
        catch
        {
            // "Recientes" es un extra del estado inicial, no el servicio del
            // palette: si falla, el palette sigue siendo plenamente usable
            // con "En esta pantalla" y con la búsqueda. Mismo criterio
            // best-effort que RegistrarUsoRecienteSilenciosamenteAsync.
        }
    }

    /// <summary>
    /// Token del ciclo de vida, o null si el componente ya se está
    /// destruyendo. Se llama SIEMPRE antes del primer await de la operación:
    /// leerlo después permitiría que un <see cref="DisposeAsync"/> intermedio
    /// hubiera desechado el CTS, y <c>.Token</c> sobre un CTS dispuesto lanza
    /// <see cref="ObjectDisposedException"/> dentro de un continuation donde
    /// nadie la recoge.
    /// </summary>
    private CancellationToken? TokenDeCicloDeVida()
    {
        var ciclo = _cicloDeVida;
        if (ciclo is null) return null;

        try
        {
            return ciclo.Token;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Tab/Shift+Tab desde el input — mueve la selección al primer elemento del grupo siguiente/anterior (Parte XVI PROMPT 05, "Tab grupo").</summary>
    [JSInvokable]
    public void SaltarGrupoDesdeJs(bool retroceder)
    {
        var inicios = InicioDeGrupo;
        if (inicios.Count == 0) return;

        if (retroceder)
        {
            var anterior = inicios.LastOrDefault(i => i < _indiceSeleccionado, inicios[^1]);
            _indiceSeleccionado = anterior;
        }
        else
        {
            var siguiente = inicios.FirstOrDefault(i => i > _indiceSeleccionado, inicios[0]);
            _indiceSeleccionado = siguiente;
        }

        StateHasChanged();
    }

    private void Cerrar()
    {
        _visible = false;

        // Cerrar es cambio de contexto: invalida lo que esté en vuelo para
        // que no se pinte sobre la próxima apertura, y borra la selección
        // para que no quede nada preparado para una entidad que ya no se ve.
        ++_generacionBusqueda;
        ++_generacionApertura;
        _indiceSeleccionado = -1;
        _resultado = null;
        _errorBusqueda = null;
        _buscando = false;

        _debounceCts?.Cancel();
    }

    private void ManejarTeclaAsync(KeyboardEventArgs e)
    {
        var elementos = ElementosPlanos;

        switch (e.Key)
        {
            case "Escape":
                Cerrar();
                break;

            case "ArrowDown" when elementos.Count > 0:
                _indiceSeleccionado = Math.Min(_indiceSeleccionado + 1, elementos.Count - 1);
                break;

            case "ArrowUp" when elementos.Count > 0:
                _indiceSeleccionado = _indiceSeleccionado <= 0 ? 0 : _indiceSeleccionado - 1;
                break;

            case "Enter" when _indiceSeleccionado >= 0 && _indiceSeleccionado < elementos.Count:
                Seleccionar(elementos[_indiceSeleccionado].Item, elementos[_indiceSeleccionado].TipoHistorial);
                break;
        }
    }

    /// <summary>
    /// Punto único de navegación del palette — usado tanto por Enter
    /// (teclado) como por el clic de cada <c>&lt;a&gt;</c> del .razor
    /// (interceptado con <c>@onclick:preventDefault</c> en vez de dejar la
    /// navegación nativa del enlace). Hacía falta centralizarlo: un enlace
    /// nativo a <c>?accion=crear</c>/<c>?accion=guardar-filtro</c> disparado
    /// desde la MISMA página que ya está montada (el caso normal de "En esta
    /// pantalla", que solo aparece estando ya en esa pantalla) usa la
    /// navegación mejorada de Blazor y reutiliza la instancia del
    /// componente — <c>OnInitializedAsync</c> nunca vuelve a ejecutarse y el
    /// query string se pierde en silencio (comprobado manualmente: "Nueva
    /// subcontrata" desde /subcontratas no abría el modal). <c>forceLoad</c>
    /// para estas rutas fuerza una recarga real del navegador, que sí
    /// remonta el componente desde cero.
    /// </summary>
    private void Seleccionar(ItemBusquedaDto item, string? tipo)
    {
        if (_navegando) return;
        _navegando = true;

        RegistrarUsoReciente(tipo, item);
        Cerrar();
        Navigation.NavigateTo(item.UrlDestino, forceLoad: RequiereNavegacionCompleta(item.UrlDestino));
    }

    /// <summary>
    /// Descargas (Exportar a Excel) y disparadores <c>?accion=...</c> que una
    /// página solo procesa en su montaje inicial (crear/guardar-filtro,
    /// ver <see cref="Seleccionar"/>) necesitan una recarga real del
    /// navegador, no la navegación SPA de Blazor.
    /// </summary>
    private static bool RequiereNavegacionCompleta(string urlDestino) =>
        urlDestino.Contains("/exportar.xlsx", StringComparison.Ordinal) ||
        urlDestino.Contains("accion=crear", StringComparison.Ordinal) ||
        urlDestino.Contains("accion=guardar-filtro", StringComparison.Ordinal);

    /// <summary>
    /// Registro de uso reciente, best-effort. <paramref name="tipo"/> null
    /// significa "Ir a": navegación pura, nunca cuenta como "uso".
    /// </summary>
    private void RegistrarUsoReciente(string? tipo, ItemBusquedaDto item)
    {
        if (tipo is null) return;

        // Fire-and-forget deliberado: el registro de recientes es
        // best-effort y nunca puede bloquear ni alterar la navegación del
        // usuario. Se lanza sin esperar y se navega/cierra de inmediato;
        // cualquier fallo se descarta en silencio. Lo que NO es opcional es
        // que viaje el token del ciclo: sin él, esta era la única llamada al
        // mediador que seguía trabajando para un componente ya retirado, en
        // contra de lo que promete el comentario de la clase.
        _ = RegistrarUsoRecienteSilenciosamenteAsync(tipo, item);
    }

    private async Task RegistrarUsoRecienteSilenciosamenteAsync(string tipo, ItemBusquedaDto item)
    {
        // El token se lee ANTES del await, como en el resto del componente.
        var token = TokenDeCicloDeVida();
        if (token is null) return;

        try
        {
            await Mediator.Send(new RegistrarUsoRecienteCommand(
                tipo, item.Id == Guid.Empty ? null : item.Id, item.Titulo, item.Subtitulo, item.UrlDestino), token.Value);
        }
        catch
        {
            // Best-effort: un fallo aquí nunca debe afectar al usuario.
        }
    }

    private async Task ManejarEntradaAsync(ChangeEventArgs e)
    {
        _termino = e.Value?.ToString() ?? string.Empty;
        _indiceSeleccionado = -1;
        _errorBusqueda = null;

        // El número de la búsqueda y el término se capturan ANTES de
        // cualquier await. El término, además, se envía desde esta copia y no
        // desde el campo: al despertar del debounce, _termino ya puede ser el
        // de una pulsación posterior, y la query saldría con un texto que no
        // es el que disparó esta búsqueda.
        var generacion = ++_generacionBusqueda;
        var termino = _termino;
        _resultado = null;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        if (termino.Trim().Length < 2 || DocumentoActual is { EsValido: false })
        {
            _resultado = null;
            _buscando = false;
            return;
        }

        // Igual que el token: se lee antes del primer await, nunca después.
        var tokenCiclo = TokenDeCicloDeVida();
        if (tokenCiclo is null) return;

        var enlazado = CancellationTokenSource.CreateLinkedTokenSource(tokenCiclo.Value);
        _debounceCts = enlazado;
        var token = enlazado.Token;

        try
        {
            _buscando = true;
            await Task.Delay(RetardoDebounce, token);

            var resultado = await Mediator.Send(new BuscarGlobalQuery(termino), token);
            if (generacion == _generacionBusqueda)
                await CargarVencenEstaSemanaAsync(termino, token);

            // Comprobación después del await, y deliberadamente NO basada en
            // el token: cancelar es una petición, no una garantía — un
            // handler que ignore el CancellationToken devolvería igualmente
            // el resultado de una búsqueda ya superada, y sin esta guarda se
            // pintaría encima de la vigente.
            if (generacion != _generacionBusqueda) return;

            _resultado = resultado;
        }
        catch (OperationCanceledException)
        {
            // Se canceló porque el usuario siguió escribiendo, cerró el
            // palette o el componente se está destruyendo — ignorar.
        }
        catch (Exception) when (generacion == _generacionBusqueda)
        {
            // Un fallo no puede acabar mostrándose como "sin resultados".
            _resultado = null;
            _errorBusqueda = "No se pudo completar la búsqueda. Vuelve a intentarlo.";
        }
        catch
        {
            // Fallo de una búsqueda ya superada: irrelevante para lo que se
            // ve, pero no puede escapar y tumbar el circuito.
        }
        finally
        {
            // La bandera de "operación en curso" solo se apaga si la
            // operación que la encendió sigue siendo la vigente: si no, el
            // final de una búsqueda superada apagaría el "Buscando…" de la
            // que está de verdad en vuelo.
            if (generacion == _generacionBusqueda)
            {
                _buscando = false;
                StateHasChanged();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        BusquedaGlobalService.SolicitudAbrir -= AbrirDesdeServicio;
        Navigation.LocationChanged -= ManejarCambioDeUbicacion;

        // Se pone a null ANTES de cancelarlo y liberarlo: cualquier lectura
        // posterior (TokenDeCicloDeVida) verá null en vez de tocar un CTS
        // desechado.
        var ciclo = _cicloDeVida;
        _cicloDeVida = null;
        ciclo?.Cancel();

        // También se anula antes de tocarlo: Blazor puede llamar a este
        // Dispose más de una vez, y Cancel() sobre un CTS ya liberado lanza
        // ObjectDisposedException.
        var debounce = _debounceCts;
        _debounceCts = null;
        debounce?.Cancel();

        // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/16-transversales.md): mismo motivo que
        // AtajosListaTeclado.razor — el circuito puede desconectarse antes
        // de que corra este Dispose.
        // Cada referencia se anula ANTES de liberarla, igual que el CTS de
        // arriba: sin esto, una segunda destrucción volvía a invocar «dispose»
        // sobre las mismas referencias ya liberadas — justo lo que el
        // comentario de arriba promete que no ocurre.
        var suscripcionAtajo = _suscripcionAtajo;
        var suscripcionTab = _suscripcionTab;
        var modulo = _modulo;
        var referencia = _referenciaDotNet;
        _suscripcionAtajo = null;
        _suscripcionTab = null;
        _modulo = null;
        _referenciaDotNet = null;

        try
        {
            if (suscripcionAtajo is not null)
            {
                await suscripcionAtajo.InvokeVoidAsync("dispose");
                await suscripcionAtajo.DisposeAsync();
            }

            if (suscripcionTab is not null)
            {
                await suscripcionTab.InvokeVoidAsync("dispose");
                await suscripcionTab.DisposeAsync();
            }

            if (modulo is not null)
                await modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        referencia?.Dispose();
        debounce?.Dispose();
        ciclo?.Dispose();
    }
}
