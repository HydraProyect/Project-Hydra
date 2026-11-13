using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

/// <param name="FechaVencimientoHasta">
/// Documentos TrabajadorId? Ambito? Busqueda? con vencimiento entre hoy y
/// esta fecha (Preventivo, Parte XVI PROMPT 03) — a diferencia de
/// <paramref name="Estado"/>, que usa los umbrales configurables del
/// tenant (UmbralAmbarDias/UmbralRojoDias), esta es una ventana temporal
/// fija elegida por quien llama (7/30/90 días); un Vencido no entra nunca
/// aquí, "próximo a vencer" excluye "ya vencido" por diseño de esta vista.
/// Se combina con <paramref name="Estado"/> si ambos llegan, aunque en la
/// práctica se usan por separado.
/// </param>
/// <param name="Estados">
/// Varios estados a la vez (la franja de estado del listado deja marcar más de uno): pasa el Documento que esté
/// en cualquiera. Se suma a <paramref name="Estado"/> si llegan los dos.
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c>: Documentos por estado con todos los demás filtros
/// aplicados y sin el de estado. Solo lo pide el listado, que es quien pinta la franja. No cuesta una consulta más:
/// la agrupación sustituye al recuento del total, que se deriva de ella sumando las claves pedidas.
/// </param>
/// <param name="TipoDocumentoId">Solo los Documentos de ese Tipo de documento.</param>
/// <param name="ProveedorPlataformaCaeId">
/// Solo los Documentos con alguna acreditación en un canal de esa plataforma CAE, en cualquier estado: los mismos
/// que pintan esa plataforma en la columna «Plataformas» del listado.
/// </param>
/// <param name="AgruparPorAmbito">Ordena primero por ámbito y calcula su resumen sobre todos los resultados filtrados.</param>
/// <param name="ExcluirCategoriaEspecialSalud">
/// Quita los documentos de salud del universo, también de los totales. Lo pide la Clave de Integración
/// del Tenant, que no los ve con ningún alcance.
/// </param>
/// <param name="AgruparPor">
/// Agrupa el listado por Plataforma CAE o por Cliente empresarial (P-1, 2026-11-12). Los «vínculos» de un Documento
/// son sus acreditaciones en Centros visibles del usuario que pasan los filtros de plataforma
/// (<paramref name="ProveedorPlataformaCaeId"/> y <paramref name="EstadosEnPlataforma"/>): por Plataforma CAE, una
/// fila por acreditación en un canal con Plataforma CAE (un Documento acreditado en tres aparece en los tres grupos);
/// por Cliente empresarial, una fila por Cliente empresarial distinto de esos Centros. Sin vínculo, el Documento va al
/// grupo <see cref="GrupoSin"/>. Los recuentos de cada grupo (<c>ResumenPorGrupo</c>, con
/// <see cref="ResumenDeGrupoDocumental"/>) se cuentan sobre el mismo universo filtrado de la página, antes de paginar.
/// <c>TotalElementos</c> sigue contando Documentos, no filas. Con esta agrupación, <paramref name="AgruparPorAmbito"/>
/// no aplica.
/// </param>
/// <param name="EstadosEnPlataforma">
/// Solo los Documentos con alguna acreditación, en un canal con Plataforma CAE de un Centro visible, en cualquiera de
/// esos estados. «Sin vigencia» es lo mismo que la pestaña Plataformas CAE llama así: aceptada y con la vigencia en la
/// plataforma sin confirmar. Si llega también <paramref name="ProveedorPlataformaCaeId"/>, la acreditación tiene que
/// ser de esa Plataforma CAE. Solo estrecha el universo ya limitado al alcance.
/// </param>
/// <param name="VenceEnDias">
/// Filtro «Vence en» (D-21): los Documentos cuyo vencimiento cae entre hoy y hoy + N días, ambos incluidos. N es un
/// valor relativo (7, 30, 90…), nunca una fecha: el servidor lo resuelve con el día de negocio de ese momento
/// (<see cref="DiaDeNegocio.Hoy()"/>, el mismo reloj con el que se calcula la franja de estado). Es el mismo universo que
/// «Por vencer» de la franja y que <paramref name="FechaVencimientoHasta"/>: con vencimiento en fecha, sin los vencidos, sin
/// los «sin caducidad» ni los «sin confirmar». Si llegan los dos, manda el límite más estricto. Un valor no positivo se ignora.
/// </param>
/// <param name="PendienteDeRevisionIa">
/// Filtro «Pendiente de revisión IA»: solo los Documentos que están hoy en la cola de Revisión IA de quien mira
/// (<see cref="ColaRevisionIa"/>, la misma consulta que la pestaña). Solo se atiende con un rol que accede a esa pestaña
/// (<see cref="ColaRevisionIa.RolesQueAccedenALaPestana"/>): con cualquier otro (Usuario de Cliente, Consulta, sin rol) el
/// servidor lo ignora, como si no hubiera llegado. El listado solo enseña Documentos operativos, así que una revisión
/// de un Documento sustituido, que la cola sí muestra, no lo incluye.
/// </param>
public record ObtenerDocumentosQuery(
    Guid? TrabajadorId, AmbitoAplicacion? Ambito, string? Busqueda, EstadoDocumento? Estado = null,
    int Pagina = 1, int TamanoPagina = 20, Guid? PropietarioId = null,
    string? OrdenarPor = null, bool Descendente = false, DateOnly? FechaVencimientoHasta = null,
    IReadOnlyCollection<EstadoDocumento>? Estados = null, bool ConRecuentosPorEstado = false,
    Guid? DocumentoId = null, Guid? TipoDocumentoId = null, Guid? ProveedorPlataformaCaeId = null,
    bool AgruparPorAmbito = false, bool ConDestinosEnCentros = false, bool ExcluirCategoriaEspecialSalud = false,
    AgrupacionDocumentos? AgruparPor = null, IReadOnlyCollection<EstadoEnPlataformaFiltro>? EstadosEnPlataforma = null,
    int? VenceEnDias = null, bool PendienteDeRevisionIa = false)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>
{
    /// <summary>Tope de <see cref="VenceEnDias"/>: ya no es «próximo a vencer», y evita desbordar la fecha.</summary>
    public const int MaximoVenceEnDias = 3650;

    /// <summary>Clave del grupo de los Documentos sin vínculo: «Sin Plataforma CAE» o «Sin Cliente empresarial».</summary>
    public const string GrupoSin = "sin";

    /// <summary>
    /// Valor de <see cref="OrdenarPor"/> para el orden de inicio del listado: lo que más urge primero (la clave de
    /// <c>EstadoDocumentalFiltro.ClaveOrden</c>) y, dentro de cada estado, por vencimiento. No es una columna:
    /// lo pide el listado mientras el usuario no elige orden. Sin <see cref="OrdenarPor"/> el orden sigue siendo
    /// el de siempre (emisión más reciente primero), que es el que conocen la API y las fichas.
    /// </summary>
    public const string OrdenPorSeveridad = "Severidad";
}

/// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — una entrada por CanalGestionDocumental aplicable, no por ProveedorPlataformaCae (el mismo proveedor puede tener más de un acceso).</summary>
/// <param name="Anotacion">
/// Lo último que se anotó a mano desde Centro 360 para este estado (fecha, motivo, nota y usuario), que se pinta bajo la
/// pastilla. Solo lo carga la lectura por Centro (<c>SituacionDocumentosEnCentrosService</c>), y solo si sigue
/// describiendo el estado actual: si otra vía cambió el estado después, la anotación ya no es de este estado y va null.
/// </param>
public record AcreditacionResumenDto(Guid Id, string NombrePlataforma, EstadoAcreditacion Estado, AnotacionEstadoPlataformaDto? Anotacion = null);

/// <summary>Un cambio de estado anotado a mano en Centro 360; <see cref="CambioId"/> es lo que «Deshacer» identifica.</summary>
public record AnotacionEstadoPlataformaDto(Guid CambioId, DateOnly Fecha, string? Motivo, string? Nota, string? UsuarioNombre);

public record DocumentoListaDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl,
    IReadOnlyList<AcreditacionResumenDto> Acreditaciones,
    // Destino del icono 360 de la fila: la página del TIPO de documento (/documentos/tipos/{id}).
    // Opcional y al final: ningún otro productor cambia; sin él (Guid.Empty) la fila no pinta el icono.
    Guid TipoDocumentoId = default,
    [property: JsonIgnore] IReadOnlyList<DocumentoEnCentroListaDto>? DestinosEnCentros = null,
    [property: JsonIgnore] GrupoDeFilaDocumentalDto? Grupo = null,
    // Id del Propietario (Trabajador, Empresa, Vehículo…) del Documento: lo necesita «Gestionar acreditación…» del menú de la fila
    // para enlazar con su ficha. Solo lectura de un dato de la propia fila: no cambia qué filas devuelve ni quién las ve.
    [property: JsonIgnore] Guid? PropietarioId = null);

/// <summary>Cómo se agrupa el listado de Documentos además de por Ámbito (ver <see cref="ObtenerDocumentosQuery.AgruparPor"/>).</summary>
public enum AgrupacionDocumentos
{
    PlataformaCae,
    ClienteEmpresarial
}

/// <summary>Filtro «Estado en la plataforma» del listado (ver <see cref="ObtenerDocumentosQuery.EstadosEnPlataforma"/>).</summary>
public enum EstadoEnPlataformaFiltro
{
    PendienteDeSubir,
    Rechazado,
    SinVigencia
}

/// <summary>
/// El grupo de una fila del listado agrupado por Plataforma CAE o por Cliente empresarial.
/// </summary>
/// <param name="Clave">El Id de la Plataforma CAE o del Cliente empresarial, o <see cref="ObtenerDocumentosQuery.GrupoSin"/>.</param>
/// <param name="Acreditacion">Por Plataforma CAE, la acreditación de esta fila; null en el grupo sin vínculo y por Cliente empresarial.</param>
public record GrupoDeFilaDocumentalDto(string Clave, AcreditacionEnGrupoDto? Acreditacion = null);

/// <param name="ClienteEmpresarialNombre">El Cliente empresarial del Centro del canal, no el Tenant propietario.</param>
/// <param name="UltimoMotivoRechazo">
/// El último motivo literal de rechazo, solo si el usuario gestiona el Centro del canal: <c>null</c> para el rol Cliente,
/// que ve el estado pero no el motivo (decisión de Chris del 2026-11-12, opción B).
/// </param>
/// <param name="UrlAccesoCanal">
/// La dirección del portal del canal de ESTA acreditación (la que usa «Abrir portal» en el menú de su fila), con el mismo corte que
/// el motivo de rechazo: solo si el usuario gestiona el Centro del canal; <c>null</c> para el rol Cliente, sin dirección anotada o si
/// el canal no la tiene. Tal como está guardada (la pantalla la normaliza y valida); es la dirección, nunca la credencial.
/// </param>
public record AcreditacionEnGrupoDto(Guid Id, EstadoAcreditacion Estado, EstadoVigenciaEnPlataforma EstadoVigencia,
    string ClienteEmpresarialNombre, string? UltimoMotivoRechazo, string? UrlAccesoCanal = null)
{
    /// <summary>La misma regla que «Sin vigencia» en la pestaña Plataformas CAE: aceptada sin vigencia confirmada.</summary>
    public bool SinVigencia => Estado == EstadoAcreditacion.Aceptada && EstadoVigencia == EstadoVigenciaEnPlataforma.SinConfirmar;
}

/// <summary>
/// Cabecera de un grupo del listado agrupado por Plataforma CAE o Cliente empresarial.
/// <see cref="ResumenDeGrupo.Total"/> son filas del grupo (acreditaciones por Plataforma CAE; Documentos por Cliente
/// empresarial y en el grupo sin vínculo); <see cref="ResumenDeGrupo.PorEstado"/> va vacío por Plataforma CAE.
/// </summary>
/// <param name="EnPlataforma">Por Plataforma CAE, acreditaciones del grupo por estado en la plataforma; sin las que son cero.</param>
/// <param name="UrlPortal">
/// La dirección del portal cuando los canales del grupo con dirección anotada, en Centros que el usuario gestiona, anotan
/// una sola (sin distinguir mayúsculas ni espacios; los canales sin dirección no cuentan); null si no hay ninguna, si hay
/// varias distintas o si el usuario no gestiona (rol Cliente). Es la dirección, nunca la credencial.
/// </param>
public record ResumenDeGrupoDocumental(int Total, IReadOnlyDictionary<string, int> PorEstado, string Nombre,
    IReadOnlyDictionary<EstadoEnPlataformaFiltro, int> EnPlataforma, string? UrlPortal = null)
    : ResumenDeGrupo(Total, PorEstado);

/// <param name="ClienteNombre">Nombre del Cliente empresarial contraparte del Centro, no del Tenant propietario.</param>
public record DocumentoEnCentroListaDto(Guid CentroId, string CentroNombre, string ClienteNombre,
    string PlataformaNombre, EstadoDocumento Estado, DateOnly? EnToleranciaHasta = null);

/// <summary>
/// Une los Documentos de Trabajador/Cliente/Empresa (cada uno vive en la
/// misma tabla pero con un único FK de propietario relleno) en una sola
/// lista paginada — se resuelven por separado (cada ámbito hace su propio
/// join al propietario que le corresponde) y se combinan con Concat antes
/// de paginar, en vez de un LEFT JOIN triple. El semáforo (Estado) se
/// calcula en memoria con CalculadoraEstadoDocumento — la misma función que
/// usan el Dashboard y RenovarDocumentoCommand — para que nunca haya dos
/// sitios de la aplicación mostrando vigencias distintas (ver Project-Hydra-Negocio/tecnico/DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext, ICurrentUserService? currentUserService = null)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>,
    IRequestHandler<ObtenerEntidadesAsociadasDeDocumentosQuery, ResultadoPaginado<EntidadAsociadaDocumentoDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        // El texto se resuelve dentro de cada rama, antes de la unión (ver ConstruirUniversoAsync): sobre la unión
        // PostgreSQL lo evaluaba una vez por Documento del Tenant.
        var consulta = await ConstruirUniversoAsync(
            string.IsNullOrWhiteSpace(request.Busqueda) ? null : request.Busqueda,
            cancellationToken);

        if (request.ExcluirCategoriaEspecialSalud)
        {
            var tiposDeSalud = tiposDocumentoContext.TiposDocumento
                .Where(t => t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud).Select(t => t.Id);
            consulta = consulta.Where(x => !tiposDeSalud.Contains(x.TipoDocumentoId));
        }

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // El constructor compartido ya acota las cinco ramas al alcance: solo estrecha.
        if (request.DocumentoId is not null)
            consulta = consulta.Where(x => x.Id == request.DocumentoId);

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.TrabajadorId == request.TrabajadorId);

        if (request.Ambito is not null)
            consulta = consulta.Where(x => x.Ambito == request.Ambito);

        if (request.PropietarioId is not null)
            consulta = consulta.Where(x => x.PropietarioId == request.PropietarioId);

        // Los dos filtros recortan la unión ya limitada al alcance del usuario: nunca la amplían.
        if (request.TipoDocumentoId is { } tipoDocumentoId)
            consulta = consulta.Where(x => x.TipoDocumentoId == tipoDocumentoId);

        var estadosEnPlataforma = request.EstadosEnPlataforma ?? [];
        var hayVinculos = request.AgruparPor is not null || estadosEnPlataforma.Count > 0;
        var centrosVisibles = request.ProveedorPlataformaCaeId is not null || hayVinculos
            ? await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken)
            : null;

        if (request.ProveedorPlataformaCaeId is { } proveedorPlataformaCaeId)
        {
            // La subconsulta se acota a los Centros visibles del usuario, igual que el destino por Centro de cada fila
            // (ObtenerDestinosEnCentrosAsync): sin ello el filtro serviría de oráculo de acreditaciones en Centros que no ve.
            var documentosEnPlataforma =
                from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
                join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
                where canal.ProveedorPlataformaCaeId == proveedorPlataformaCaeId
                    && (centrosVisibles == null || centrosVisibles.Contains(canal.CentroId))
                select acreditacion.DocumentoId;
            consulta = consulta.Where(x => documentosEnPlataforma.Contains(x.Id));
        }

        // Los vínculos con Plataforma CAE y Cliente empresarial (agrupar y «Estado en la plataforma") salen de la misma
        // regla de alcance que el filtro de arriba: acreditaciones en Centros visibles. Con el filtro de estado o al
        // agrupar por Plataforma CAE solo cuentan los canales con Plataforma CAE; por Cliente empresarial, cualquier canal.
        var vinculos = hayVinculos
            ? ConstruirVinculos(centrosVisibles, request.ProveedorPlataformaCaeId, estadosEnPlataforma,
                soloConPlataformaCae: request.AgruparPor != AgrupacionDocumentos.ClienteEmpresarial || estadosEnPlataforma.Count > 0)
            : null;

        if (estadosEnPlataforma.Count > 0)
        {
            var documentosEnEstado = vinculos!.Select(v => v.DocumentoId);
            consulta = consulta.Where(x => documentosEnEstado.Contains(x.Id));
        }

        // «Pendiente de revisión IA»: la cola de la pestaña Revisión IA, con su alcance, y solo para los roles que acceden a
        // ella. Con otro rol el filtro no llega a la consulta (no la estrecha ni la vacía): se ignora. Sin rol conocido,
        // falla cerrado. Solo estrecha el universo ya acotado por el alcance de cada rama.
        if (request.PendienteDeRevisionIa && currentUserService is not null
            && ColaRevisionIa.AccedeElRol(await currentUserService.ObtenerRolEfectivoAsync()))
        {
            var trabajadorIdsEnCola = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
            var documentosEnCola = ColaRevisionIa.DocumentoIds(documentosContext, trabajadorIdsEnCola);
            consulta = consulta.Where(x => documentosEnCola.Contains(x.Id));
        }

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Equivalencias con CalculadoraEstadoDocumento, que compara "días
        // restantes" contra los umbrales: días <= umbral es lo mismo que
        // fechaVencimiento <= hoy + umbral. Las usan tanto el filtro por
        // Estado como el orden por Estado, así que se calculan una vez.
        var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
        var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

        // El Estado se sigue calculando con CalculadoraEstadoDocumento —
        // fuente única de verdad, ver el comentario de clase—, pero el
        // *filtro* por Estado se traduce a un rango de fechas equivalente
        // para que SQL pueda hacerlo. Antes no se traducía, y eso obligaba a
        // materializar todos los Documentos del tenant en cada carga de la
        // pantalla para paginar en memoria.
        // «Vence en N días» (D-21) es la misma ventana que FechaVencimientoHasta, con el límite calculado aquí, con el día
        // de negocio de esta petición: lo recordado y lo guardado es N, nunca una fecha que envejezca.
        var hasta = request.FechaVencimientoHasta;
        if (request.VenceEnDias is > 0 and var venceEnDias)
        {
            var porDias = hoy.AddDays(Math.Min(venceEnDias, ObtenerDocumentosQuery.MaximoVenceEnDias));
            hasta = hasta is { } limiteDado && limiteDado < porDias ? limiteDado : porDias;
        }

        if (hasta is { } limite)
            consulta = consulta.Where(x => x.FechaVencimiento != null && x.FechaVencimiento >= hoy && x.FechaVencimiento <= limite);

        // La clave de estado es el mismo CASE de umbrales con el que se ordena más abajo (el orden de
        // EstadoDocumentalFiltro.ClaveOrden: 0 Vencido, 1 Urgente, 2 Próximo, 3 Sin confirmar, 4 Vigente,
        // 5 Sin caducidad). «No caduca» y «sin confirmar» se distinguen por EstadoVigencia, nunca por una fecha
        // nula. Contar, filtrar y ordenar por la misma expresión garantiza que parten los Documentos igual.
        //
        // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada cifra
        // de la franja dice cuántos Documentos quedarían al marcar solo ese estado.
        IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
        Dictionary<int, int>? filasPorClave = null;
        if (request.ConRecuentosPorEstado)
        {
            filasPorClave = await consulta
                .GroupBy(x =>
                    x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4)
                .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken);
            recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(filasPorClave);
        }

        // El Estado se sigue calculando con CalculadoraEstadoDocumento —
        // fuente única de verdad, ver el comentario de clase—, pero el
        // *filtro* por Estado se traduce a esa clave, equivalente a un rango
        // de fechas, para que SQL pueda hacerlo. Antes no se traducía, y eso
        // obligaba a materializar todos los Documentos del tenant en cada
        // carga de la pantalla para paginar en memoria. Si alguna vez cambia
        // la calculadora, la clave cambia con ella —
        // DocumentosPaginacionEnSqlTests compara ambas para que no se separen
        // en silencio.
        var estadosPedidos = (request.Estados ?? []).ToList();
        if (request.Estado is { } unEstado && !estadosPedidos.Contains(unEstado))
            estadosPedidos.Add(unEstado);

        List<int>? clavesPedidas = null;
        if (estadosPedidos.Count > 0)
        {
            // «En tolerancia» es un estado de contexto de Centro (VigenciaEnCentro) y Faltante solo lo emiten las
            // Alertas: esta lista nunca los produce, así que su clave (6) no coincide con ninguna fila. Filtrar
            // solo por ellos no devuelve nada (y no «todo», que sacaría los vencidos con otro rótulo).
            clavesPedidas = estadosPedidos.Select(estado => EstadoDocumentalFiltro.ClaveOrden(estado)).Distinct().ToList();
            consulta = consulta.Where(x => clavesPedidas.Contains(
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4));
        }

        // Con los recuentos ya leídos, el total no necesita otra pasada por la unión de las cinco ramas: el filtro
        // por estado es «clave ∈ claves pedidas» sobre la misma expresión y el mismo universo con que se agrupó, así
        // que el total es la suma de esas claves (o de todas, sin filtro). Una lectura del universo menos por página.
        var total = filasPorClave is null
            ? await consulta.CountAsync(cancellationToken)
            : filasPorClave.Where(x => clavesPedidas is null || clavesPedidas.Contains(x.Key)).Sum(x => x.Value);

        var agruparPorAmbito = request.AgruparPorAmbito && request.AgruparPor is null;
        IReadOnlyDictionary<string, ResumenDeGrupo>? resumenPorGrupo = null;
        Dictionary<Guid, string> nombresDeGrupo = [];
        if (request.AgruparPor is { } agrupacion)
            (resumenPorGrupo, nombresDeGrupo) = await ResumirGruposAsync(
                agrupacion, consulta, vinculos!, hoy, limiteRojo, limiteAmbar, cancellationToken);
        else if (agruparPorAmbito)
        {
            // Agregados del mismo universo filtrado antes de paginar; nunca un recuento de la página visible.
            var agregados = await consulta.GroupBy(x => new
            {
                x.Ambito,
                ClaveEstado = x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                        : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                        : x.FechaVencimiento < hoy ? 0
                        : x.FechaVencimiento <= limiteRojo ? 1
                        : x.FechaVencimiento <= limiteAmbar ? 2
                        : 4
            })
                .Select(g => new { g.Key.Ambito, g.Key.ClaveEstado, Total = g.Count() })
                .ToListAsync(cancellationToken);
            resumenPorGrupo = agregados.GroupBy(x => x.Ambito).ToDictionary(
                g => g.Key.ToString(),
                g => new ResumenDeGrupo(g.Sum(x => x.Total),
                    EstadoDocumentalFiltro.RecuentosPorEstado(g.ToDictionary(x => x.ClaveEstado, x => x.Total))
                        .Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value)));
        }

        IOrderedQueryable<FilaDocumental> OrdenarPor<TClave>(Expression<Func<FilaDocumental, TClave>> clave, bool descendente = false)
        {
            if (agruparPorAmbito)
            {
                var agrupada = consulta.OrderBy(x => x.Ambito);
                return descendente ? agrupada.ThenByDescending(clave) : agrupada.ThenBy(clave);
            }
            return descendente ? consulta.OrderByDescending(clave) : consulta.OrderBy(clave);
        }

        // Lista blanca de columnas ordenables (nombres de propiedad de
        // DocumentoListaDto, que es lo que envía QuickGrid): un OrdenarPor
        // desconocido cae al orden por defecto, nunca se interpola en SQL.
        // "Estado" no está persistido, pero sí lo está FechaVencimiento, así
        // que se ordena por el mismo CASE de umbrales que usa el filtro de
        // arriba — exacto y resuelto en la base de datos. Ascendente deja
        // primero lo que más urge, que es lo que el gestor espera del primer
        // clic en esa cabecera; el orden es el de
        // EstadoDocumentalFiltro.ClaveOrden (lo sin confirmar entre lo malo
        // conocido y lo vigente).
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(DocumentoListaDto.PropietarioNombre), false) => OrdenarPor(x => x.PropietarioNombre),
            (nameof(DocumentoListaDto.PropietarioNombre), true) => OrdenarPor(x => x.PropietarioNombre, true),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), false) => OrdenarPor(x => x.TipoDocumentoNombre),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), true) => OrdenarPor(x => x.TipoDocumentoNombre, true),
            (nameof(DocumentoListaDto.Ambito), false) => OrdenarPor(x => x.Ambito),
            (nameof(DocumentoListaDto.Ambito), true) => OrdenarPor(x => x.Ambito, true),
            (nameof(DocumentoListaDto.FechaEmision), false) => OrdenarPor(x => x.FechaEmision),
            (nameof(DocumentoListaDto.FechaEmision), true) => OrdenarPor(x => x.FechaEmision, true),
            (nameof(DocumentoListaDto.FechaVencimiento), false) => OrdenarPor(x => x.FechaVencimiento),
            (nameof(DocumentoListaDto.FechaVencimiento), true) => OrdenarPor(x => x.FechaVencimiento, true),
            (nameof(DocumentoListaDto.Estado), false) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4),
            (nameof(DocumentoListaDto.Estado), true) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4, true),
            // Orden de inicio del listado: la misma clave de estado y, dentro de cada estado, lo que antes vence.
            (ObtenerDocumentosQuery.OrdenPorSeveridad, _) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4).ThenBy(x => x.FechaVencimiento),
            _ => OrdenarPor(x => x.FechaEmision, true)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.Id);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new FilaDocumental
            {
                Id = x.Id,
                Ambito = x.Ambito,
                PropietarioId = x.PropietarioId,
                PropietarioNombre = x.PropietarioNombre,
                TipoDocumentoNombre = x.TipoDocumentoNombre,
                TipoDocumentoId = x.TipoDocumentoId,
                FechaEmision = x.FechaEmision,
                EstadoVigencia = x.EstadoVigencia,
                FechaVencimiento = x.FechaVencimiento,
                ArchivoUrl = x.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        // Ahora solo sobre la página, no sobre todo el tenant.
        var documentoIds = pagina.Select(d => d.Id).ToList();
        var acreditacionesPorDocumento = await ObtenerAcreditacionesPorDocumentoAsync(documentoIds, cancellationToken);
        var destinos = request.ConDestinosEnCentros
            ? await ObtenerDestinosEnCentrosAsync(pagina, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken)
            : null;

        var elementos = pagina
            .Select(d => new DocumentoListaDto(
                d.Id, d.Ambito, d.PropietarioNombre, d.TipoDocumentoNombre, d.FechaEmision, d.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    d.EstadoVigencia, d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                d.ArchivoUrl,
                acreditacionesPorDocumento.GetValueOrDefault(d.Id, []),
                d.TipoDocumentoId,
                destinos?.GetValueOrDefault(d.Id, []),
                PropietarioId: d.PropietarioId))
            .ToList();

        if (request.AgruparPor is { } agrupacionDeFilas)
            elementos = await RepartirEnGruposAsync(agrupacionDeFilas, elementos, vinculos!, nombresDeGrupo, cancellationToken);

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado,
            ResumenPorGrupo = resumenPorGrupo
        };
    }

    public async Task<ResultadoPaginado<EntidadAsociadaDocumentoDto>> Handle(
        ObtenerEntidadesAsociadasDeDocumentosQuery request, CancellationToken cancellationToken)
    {
        var consulta = await ConstruirUniversoAsync(null, cancellationToken);
        if (request.Ambito is { } ambito)
            consulta = consulta.Where(x => x.Ambito == ambito);
        if (!string.IsNullOrWhiteSpace(request.Busqueda))
            consulta = consulta.Where(x => TextoDeBusqueda.Contiene(x.PropietarioNombre, request.Busqueda));

        var opciones = consulta.Where(x => x.PropietarioId != null)
            .Select(x => new { Id = x.PropietarioId!.Value, x.Ambito, Nombre = x.PropietarioNombre })
            .Distinct();
        var total = await opciones.CountAsync(cancellationToken);
        var pagina = Math.Max(1, request.Pagina);
        var tamano = Math.Clamp(request.TamanoPagina, 1, 50);
        var elementos = await opciones.OrderBy(x => x.Nombre).ThenBy(x => x.Ambito).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(x => new EntidadAsociadaDocumentoDto(x.Id, x.Ambito, x.Nombre))
            .ToListAsync(cancellationToken);
        return new(elementos, total, pagina, tamano);
    }

    // Ambas lecturas comparten documentos operativos y restricciones de visibilidad por ámbito.
    //
    // El filtro de texto va dentro de cada rama y por identificador, no sobre la unión: primero los titulares y los
    // Tipos que casan (unos miles de Trabajadores y unos cientos de Tipos, Empresas, Vehículos y Proyectos, con la
    // misma expresión que la rama pinta como PropietarioNombre) y después los Documentos de esos ids. Sobre la unión,
    // PostgreSQL no podía bajar el predicado a las tablas de titulares y evaluaba texto_de_busqueda dos veces por
    // Documento del Tenant (9,3 s con 500.000). Mismo resultado: la rama sigue uniendo con su titular y su Tipo, así
    // que «su titular está entre los que casan» es lo mismo que «el nombre unido casa». El alcance de cada rama
    // (cartera, RLS) no cambia: el filtro de texto solo estrecha. Las subconsultas viajan en la misma sentencia.
    private async Task<IQueryable<FilaDocumental>> ConstruirUniversoAsync(string? busqueda, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in documentosContext.Documentos.Operativos()
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Trabajador,
                TrabajadorId = (Guid?)documento.TrabajadorId,
                PropietarioId = (Guid?)documento.TrabajadorId,
                PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = documento.TipoDocumentoId,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deCliente =
            from documento in documentosContext.Documentos.Operativos()
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            // Documento.ClienteId ya apunta a Empresas (F3): el propietario "Cliente"
            // de un Documento se resuelve contra Empresas, no contra Clientes (congelada).
            join cliente in empresasContext.Empresas on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Cliente,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ClienteId,
                PropietarioNombre = cliente.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = documento.TipoDocumentoId,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deEmpresa =
            from documento in documentosContext.Documentos.Operativos()
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in empresasContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Empresa,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.EmpresaId,
                PropietarioNombre = empresa.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = documento.TipoDocumentoId,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deVehiculo =
            from documento in documentosContext.Documentos.Operativos()
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in vehiculosContext.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Vehiculo,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.VehiculoId,
                PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")",
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = documento.TipoDocumentoId,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deProyecto =
            from documento in documentosContext.Documentos.Operativos()
            where documento.ProyectoId != null
            join proyecto in proyectosContext.Proyectos on documento.ProyectoId!.Value equals proyecto.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(proyecto.ClienteId)
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Proyecto,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ProyectoId,
                PropietarioNombre = proyecto.Nombre,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = documento.TipoDocumentoId,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        // El buscador del listado: casa el Documento cuyo titular (su PropietarioNombre) o cuyo Tipo de documento contienen
        // el texto, sin acentos ni mayúsculas.
        if (busqueda is { } texto)
        {
            // Cada expresión es la que su rama pinta como PropietarioNombre: el buscador casa con lo que se ve.
            var trabajadoresQueCasan = trabajadoresContext.Trabajadores
                .Where(x => TextoDeBusqueda.Contiene(x.Nombre + " " + x.Apellidos, texto)).Select(x => x.Id);
            var empresasQueCasan = empresasContext.Empresas
                .Where(x => TextoDeBusqueda.Contiene(x.RazonSocial, texto)).Select(x => x.Id);
            var vehiculosQueCasan = vehiculosContext.Vehiculos
                .Where(x => TextoDeBusqueda.Contiene(x.Nombre + " (" + x.NumeroPlaca + ")", texto)).Select(x => x.Id);
            var proyectosQueCasan = proyectosContext.Proyectos
                .Where(x => TextoDeBusqueda.Contiene(x.Nombre, texto)).Select(x => x.Id);
            var tiposQueCasan = tiposDocumentoContext.TiposDocumento
                .Where(x => TextoDeBusqueda.Contiene(x.Nombre, texto)).Select(x => x.Id);

            // PropietarioId y TipoDocumentoId son columnas del propio Documento en las cinco ramas, así que el filtro
            // baja a la lectura de Documentos, antes de unir con el titular y el Tipo.
            IQueryable<FilaDocumental> CasanConElTexto(IQueryable<FilaDocumental> rama, IQueryable<Guid> titularesQueCasan) =>
                rama.Where(x => titularesQueCasan.Contains(x.PropietarioId!.Value) || tiposQueCasan.Contains(x.TipoDocumentoId));

            deTrabajador = CasanConElTexto(deTrabajador, trabajadoresQueCasan);
            deCliente = CasanConElTexto(deCliente, empresasQueCasan);
            deEmpresa = CasanConElTexto(deEmpresa, empresasQueCasan);
            deVehiculo = CasanConElTexto(deVehiculo, vehiculosQueCasan);
            deProyecto = CasanConElTexto(deProyecto, proyectosQueCasan);
        }

        return deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo).Concat(deProyecto);

    }

    /// <summary>
    /// Las acreditaciones que enlazan un Documento con una Plataforma CAE y un Cliente empresarial: solo en Centros
    /// visibles (el mismo alcance que el filtro por Plataforma CAE y los destinos por Centro), y solo las que pasan los
    /// filtros de plataforma de la petición. Es la única fuente de los grupos, de sus recuentos y del filtro de estado.
    /// </summary>
    private IQueryable<VinculoDocumental> ConstruirVinculos(IReadOnlyList<Guid>? centrosVisibles, Guid? proveedorId,
        IReadOnlyCollection<EstadoEnPlataformaFiltro> estados, bool soloConPlataformaCae)
    {
        var vinculos =
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            where centrosVisibles == null || centrosVisibles.Contains(centro.Id)
            // P1-X2, como la pestaña Plataformas CAE (ObtenerAcreditacionesPorProveedorQuery): los canales que conserve un
            // Centro sin gestión CAE no generan trabajo de acreditación, ni grupo, ni recuento, ni «Anotar vigencia».
            where centro.GestionCae != ModalidadGestionCae.SinGestionCae
            select new VinculoDocumental
            {
                AcreditacionId = acreditacion.Id,
                DocumentoId = acreditacion.DocumentoId,
                Estado = acreditacion.Estado,
                EstadoVigencia = acreditacion.EstadoVigencia,
                ProveedorId = canal.ProveedorPlataformaCaeId,
                CentroId = centro.Id,
                ClienteId = centro.ClienteId,
                UrlAcceso = canal.UrlAcceso
            };

        if (soloConPlataformaCae)
            vinculos = vinculos.Where(v => v.ProveedorId != null);
        if (proveedorId is { } proveedor)
            vinculos = vinculos.Where(v => v.ProveedorId == proveedor);
        if (estados.Count > 0)
        {
            var pendiente = estados.Contains(EstadoEnPlataformaFiltro.PendienteDeSubir);
            var rechazado = estados.Contains(EstadoEnPlataformaFiltro.Rechazado);
            var sinVigencia = estados.Contains(EstadoEnPlataformaFiltro.SinVigencia);
            vinculos = vinculos.Where(v =>
                (pendiente && v.Estado == EstadoAcreditacion.PendienteDeSubir)
                || (rechazado && v.Estado == EstadoAcreditacion.Rechazada)
                || (sinVigencia && v.Estado == EstadoAcreditacion.Aceptada && v.EstadoVigencia == EstadoVigenciaEnPlataforma.SinConfirmar));
        }

        return vinculos;
    }

    /// <summary>
    /// Las cabeceras de los grupos, contadas sobre el universo ya filtrado (el mismo <paramref name="consulta"/> que se
    /// pagina, después de todos los filtros, también el de estado) unido a sus vínculos: nunca sobre la página visible
    /// ni sobre otra consulta. Devuelve también los nombres de los grupos, que reutiliza el reparto de filas.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, ResumenDeGrupo>, Dictionary<Guid, string>)> ResumirGruposAsync(
        AgrupacionDocumentos agrupacion, IQueryable<FilaDocumental> consulta, IQueryable<VinculoDocumental> vinculos,
        DateOnly hoy, DateOnly limiteRojo, DateOnly limiteAmbar, CancellationToken cancellationToken)
    {
        var resumen = new Dictionary<string, ResumenDeGrupo>();
        Dictionary<Guid, string> nombres;
        var ninguno = new Dictionary<EstadoEnPlataformaFiltro, int>();

        if (agrupacion == AgrupacionDocumentos.PlataformaCae)
        {
            var filas = await (
                from fila in consulta
                join vinculo in vinculos on fila.Id equals vinculo.DocumentoId
                group vinculo by new { vinculo.ProveedorId, vinculo.Estado, vinculo.EstadoVigencia } into grupo
                select new { grupo.Key.ProveedorId, grupo.Key.Estado, grupo.Key.EstadoVigencia, Filas = grupo.Count() })
                .ToListAsync(cancellationToken);

            // «Abrir portal» lleva la dirección del canal, como la fila de la pestaña Plataformas CAE, y con su mismo
            // alcance: Centros que el usuario gestiona (el rol Cliente lee, no opera, y no la recibe).
            var centrosGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
            var urls = await (
                from fila in consulta
                join vinculo in vinculos on fila.Id equals vinculo.DocumentoId
                where vinculo.UrlAcceso != null && (centrosGestion == null || centrosGestion.Contains(vinculo.CentroId))
                select new { vinculo.ProveedorId, vinculo.UrlAcceso })
                .Distinct()
                .ToListAsync(cancellationToken);

            var proveedorIds = filas.Where(f => f.ProveedorId != null).Select(f => f.ProveedorId!.Value).Distinct().ToList();
            nombres = await proveedoresContext.ProveedoresPlataformaCae.Where(p => proveedorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

            foreach (var grupo in filas.Where(f => f.ProveedorId != null).GroupBy(f => f.ProveedorId!.Value))
            {
                var enPlataforma = new Dictionary<EstadoEnPlataformaFiltro, int>();
                void Anotar(EstadoEnPlataformaFiltro estado, int cuantas)
                {
                    if (cuantas > 0) enPlataforma[estado] = cuantas;
                }
                Anotar(EstadoEnPlataformaFiltro.PendienteDeSubir,
                    grupo.Where(f => f.Estado == EstadoAcreditacion.PendienteDeSubir).Sum(f => f.Filas));
                Anotar(EstadoEnPlataformaFiltro.Rechazado,
                    grupo.Where(f => f.Estado == EstadoAcreditacion.Rechazada).Sum(f => f.Filas));
                Anotar(EstadoEnPlataformaFiltro.SinVigencia,
                    grupo.Where(f => f.Estado == EstadoAcreditacion.Aceptada && f.EstadoVigencia == EstadoVigenciaEnPlataforma.SinConfirmar)
                        .Sum(f => f.Filas));
                // Orden ordinal antes de quitar duplicados: entre dos grafías de la misma dirección gana siempre la misma,
                // sin depender del orden en que la base de datos devuelva el DISTINCT.
                var urlsDelGrupo = urls.Where(u => u.ProveedorId == grupo.Key)
                    .Select(u => u.UrlAcceso!.Trim()).Where(u => u.Length > 0)
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                resumen[grupo.Key.ToString()] = new ResumenDeGrupoDocumental(
                    grupo.Sum(f => f.Filas), new Dictionary<string, int>(), nombres.GetValueOrDefault(grupo.Key, "Plataforma"),
                    enPlataforma, urlsDelGrupo.Count == 1 ? urlsDelGrupo[0] : null);
            }
        }
        else
        {
            // Un Documento cuenta una vez por Cliente empresarial aunque esté acreditado en varios Centros suyos.
            var filas = await (
                from fila in consulta
                join vinculo in vinculos on fila.Id equals vinculo.DocumentoId
                select new
                {
                    vinculo.ClienteId,
                    fila.Id,
                    ClaveEstado = fila.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                        : fila.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                        : fila.FechaVencimiento < hoy ? 0
                        : fila.FechaVencimiento <= limiteRojo ? 1
                        : fila.FechaVencimiento <= limiteAmbar ? 2
                        : 4
                })
                .Distinct()
                .GroupBy(x => new { x.ClienteId, x.ClaveEstado })
                .Select(grupo => new { grupo.Key.ClienteId, grupo.Key.ClaveEstado, Total = grupo.Count() })
                .ToListAsync(cancellationToken);

            var clienteIds = filas.Select(f => f.ClienteId).Distinct().ToList();
            nombres = await empresasContext.Empresas.Where(e => clienteIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

            foreach (var grupo in filas.GroupBy(f => f.ClienteId))
                resumen[grupo.Key.ToString()] = new ResumenDeGrupoDocumental(
                    grupo.Sum(f => f.Total),
                    EstadoDocumentalFiltro.RecuentosPorEstado(grupo.ToDictionary(f => f.ClaveEstado, f => f.Total))
                        .Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value),
                    nombres.GetValueOrDefault(grupo.Key, "—"), ninguno);
        }

        var documentosConVinculo = vinculos.Select(v => v.DocumentoId);
        var sinVinculo = await consulta.Where(x => !documentosConVinculo.Contains(x.Id)).CountAsync(cancellationToken);
        if (sinVinculo > 0)
            resumen[ObtenerDocumentosQuery.GrupoSin] = new ResumenDeGrupoDocumental(
                sinVinculo, new Dictionary<string, int>(), string.Empty, ninguno);

        return (resumen, nombres);
    }

    /// <summary>
    /// Reparte la página en filas de grupo: por Plataforma CAE, una por acreditación (con su estado, su Cliente
    /// empresarial y el último motivo de rechazo); por Cliente empresarial, una por Cliente empresarial distinto. Sin
    /// vínculo, una fila en <see cref="ObtenerDocumentosQuery.GrupoSin"/>. Mismos vínculos que los recuentos.
    /// </summary>
    private async Task<List<DocumentoListaDto>> RepartirEnGruposAsync(AgrupacionDocumentos agrupacion,
        List<DocumentoListaDto> elementos, IQueryable<VinculoDocumental> vinculos, Dictionary<Guid, string> nombresDeGrupo,
        CancellationToken cancellationToken)
    {
        if (elementos.Count == 0) return elementos;
        var documentoIds = elementos.Select(e => e.Id).ToList();
        var dePagina = await vinculos.Where(v => documentoIds.Contains(v.DocumentoId)).ToListAsync(cancellationToken);
        var porDocumento = dePagina.ToLookup(v => v.DocumentoId);
        var filas = new List<DocumentoListaDto>(elementos.Count);
        var sinVinculo = new GrupoDeFilaDocumentalDto(ObtenerDocumentosQuery.GrupoSin);

        if (agrupacion == AgrupacionDocumentos.ClienteEmpresarial)
        {
            foreach (var elemento in elementos)
            {
                var clientes = porDocumento[elemento.Id].Select(v => v.ClienteId).Distinct()
                    .OrderBy(id => nombresDeGrupo.GetValueOrDefault(id, string.Empty)).ThenBy(id => id).ToList();
                if (clientes.Count == 0) filas.Add(elemento with { Grupo = sinVinculo });
                else filas.AddRange(clientes.Select(id => elemento with { Grupo = new GrupoDeFilaDocumentalDto(id.ToString()) }));
            }
            return filas;
        }

        // El motivo literal de rechazo, solo en Centros que el usuario gestiona: el rol Cliente ve el estado en la
        // plataforma de las acreditaciones de sus Centros visibles, pero no el motivo (decisión de Chris del 2026-11-12,
        // opción B). Es el mismo corte que «Abrir portal» (ObtenerCentroIdsParaGestionAsync: lista vacía para el rol
        // Cliente, fallo cerrado), y se aplica aquí y no en la vista: el motivo ni se lee ni viaja en el DTO.
        var centrosGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        var rechazadas = dePagina
            .Where(v => v.Estado == EstadoAcreditacion.Rechazada && (centrosGestion == null || centrosGestion.Contains(v.CentroId)))
            .Select(v => v.AcreditacionId).ToList();
        var motivos = rechazadas.Count == 0
            ? new Dictionary<Guid, string>()
            : await documentosContext.RechazosAcreditacionDocumentoPlataforma
                .Where(r => rechazadas.Contains(r.AcreditacionId))
                .OrderByDescending(r => r.FechaUtc)
                .GroupBy(r => r.AcreditacionId)
                .Select(g => new { AcreditacionId = g.Key, Motivo = g.First().MotivoLiteral })
                .ToDictionaryAsync(x => x.AcreditacionId, x => x.Motivo, cancellationToken);
        var clienteIds = dePagina.Select(v => v.ClienteId).Distinct().ToList();
        var clientesEmpresariales = await empresasContext.Empresas.Where(e => clienteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        foreach (var elemento in elementos)
        {
            var suyas = porDocumento[elemento.Id]
                .OrderBy(v => nombresDeGrupo.GetValueOrDefault(v.ProveedorId!.Value, string.Empty))
                .ThenBy(v => clientesEmpresariales.GetValueOrDefault(v.ClienteId, string.Empty))
                .ThenBy(v => v.AcreditacionId)
                .ToList();
            if (suyas.Count == 0)
            {
                filas.Add(elemento with { Grupo = sinVinculo });
                continue;
            }
            filas.AddRange(suyas.Select(v => elemento with
            {
                Grupo = new GrupoDeFilaDocumentalDto(v.ProveedorId!.Value.ToString(), new AcreditacionEnGrupoDto(
                    v.AcreditacionId, v.Estado, v.EstadoVigencia, clientesEmpresariales.GetValueOrDefault(v.ClienteId, "—"),
                    motivos.GetValueOrDefault(v.AcreditacionId),
                    // El mismo corte que el motivo: Centros que el usuario gestiona; el rol Cliente no recibe la dirección.
                    centrosGestion == null || centrosGestion.Contains(v.CentroId) ? v.UrlAcceso : null))
            }));
        }
        return filas;
    }

    private sealed class VinculoDocumental
    {
        public Guid AcreditacionId { get; init; }
        public Guid DocumentoId { get; init; }
        public EstadoAcreditacion Estado { get; init; }
        public EstadoVigenciaEnPlataforma EstadoVigencia { get; init; }
        public Guid? ProveedorId { get; init; }
        public Guid CentroId { get; init; }
        public Guid ClienteId { get; init; }
        public string? UrlAcceso { get; init; }
    }

    private sealed class FilaDocumental
    {
        public Guid Id { get; init; }
        public AmbitoAplicacion Ambito { get; init; }
        public Guid? TrabajadorId { get; init; }
        public Guid? PropietarioId { get; init; }
        public string PropietarioNombre { get; init; } = string.Empty;
        public string TipoDocumentoNombre { get; init; } = string.Empty;
        public Guid TipoDocumentoId { get; init; }
        public DateOnly FechaEmision { get; init; }
        public EstadoVigenciaDocumento EstadoVigencia { get; init; }
        public DateOnly? FechaVencimiento { get; init; }
        public string? ArchivoUrl { get; init; }
    }

    private async Task<Dictionary<Guid, List<DocumentoEnCentroListaDto>>> ObtenerDestinosEnCentrosAsync(
        IReadOnlyList<FilaDocumental> pagina, DateOnly hoy, int umbralAmbar, int umbralRojo, CancellationToken cancellationToken)
    {
        if (pagina.Count == 0) return [];
        var documentoIds = pagina.Select(x => x.Id).ToList();
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where documentoIds.Contains(acreditacion.DocumentoId)
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            where centrosVisibles == null || centrosVisibles.Contains(centro.Id)
            join clienteEmpresarial in empresasContext.Empresas on centro.ClienteId equals clienteEmpresarial.Id
            select new
            {
                acreditacion.DocumentoId,
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                ClienteEmpresarialId = clienteEmpresarial.Id,
                ClienteNombre = clienteEmpresarial.RazonSocial,
                canal.ProveedorPlataformaCaeId,
                canal.Tipo
            }).ToListAsync(cancellationToken);
        if (crudas.Count == 0) return [];

        var centroIds = crudas.Select(x => x.CentroId).Distinct().ToList();
        var clienteIds = crudas.Select(x => x.ClienteEmpresarialId).Distinct().ToList();
        var tipoIds = pagina.Select(x => x.TipoDocumentoId).Distinct().ToList();
        var proveedorIds = crudas.Where(x => x.ProveedorPlataformaCaeId != null)
            .Select(x => x.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae.Where(x => proveedorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Nombre, cancellationToken);
        var filasCentro = await tiposDocumentoContext.TiposDocumentoCentros
            .Where(x => centroIds.Contains(x.CentroId) && tipoIds.Contains(x.TipoDocumentoId))
            .ToListAsync(cancellationToken);
        var condicionesCentro = filasCentro.ToDictionary(x => (x.CentroId, x.TipoDocumentoId));
        var tolerancias = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, clienteIds, tipoIds, cancellationToken);
        var documentos = pagina.ToDictionary(x => x.Id);

        return crudas.GroupBy(x => x.DocumentoId).ToDictionary(g => g.Key, g => g.Select(x =>
        {
            var documento = documentos[x.DocumentoId];
            var propio = CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento,
                hoy, umbralAmbar, umbralRojo);
            var condiciones = VigenciaEnCentro.Condiciones(
                condicionesCentro.GetValueOrDefault((x.CentroId, documento.TipoDocumentoId)),
                tolerancias.TryGetValue((x.ClienteEmpresarialId, documento.TipoDocumentoId), out var dias) ? dias : null);
            var contextual = VigenciaEnCentro.Aplicar(propio, documento.EstadoVigencia, documento.FechaVencimiento,
                documento.FechaEmision, condiciones, hoy);
            var plataforma = x.ProveedorPlataformaCaeId is { } proveedorId
                ? proveedores.GetValueOrDefault(proveedorId, "Plataforma")
                : x.Tipo == TipoCanalGestion.Email ? "Sin portal" : "Plataforma";
            return new DocumentoEnCentroListaDto(x.CentroId, x.CentroNombre, x.ClienteNombre,
                plataforma, contextual.Estado, contextual.EnToleranciaHasta);
        }).Distinct().OrderBy(x => x.CentroNombre).ThenBy(x => x.PlataformaNombre).ThenBy(x => x.CentroId).ToList());
    }

    /// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — badges por plataforma. Solo sobre la página ya paginada, nunca sobre todo el tenant.</summary>
    private async Task<Dictionary<Guid, List<AcreditacionResumenDto>>> ObtenerAcreditacionesPorDocumentoAsync(
        List<Guid> documentoIds, CancellationToken cancellationToken)
    {
        if (documentoIds.Count == 0) return [];

        // El LEFT JOIN conserva los canales sin proveedor (p. ej. por correo), que se rotulan «Plataforma».
        // Proyectar el nombre aquí evita una segunda lectura secuencial por página.
        // Solo las acreditaciones de Centros visibles del usuario (ASVS 4.0.3 V4.2.1), igual que el destino por Centro
        // (ObtenerDestinosEnCentrosAsync) y el filtro por Plataforma CAE: sin ello la fila revelaría la plataforma y el
        // estado de acreditación en Centros fuera de su Asignación de Cartera o de su Cliente empresarial.
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where documentoIds.Contains(acreditacion.DocumentoId)
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            where centrosVisibles == null || centrosVisibles.Contains(canal.CentroId)
            join proveedor in proveedoresContext.ProveedoresPlataformaCae
                on canal.ProveedorPlataformaCaeId equals (Guid?)proveedor.Id into proveedores
            from proveedor in proveedores.DefaultIfEmpty()
            select new
            {
                acreditacion.DocumentoId,
                acreditacion.Id,
                acreditacion.Estado,
                NombrePlataforma = proveedor == null ? "Plataforma" : proveedor.Nombre
            }).ToListAsync(cancellationToken);

        return crudas.GroupBy(c => c.DocumentoId).ToDictionary(
            g => g.Key,
            g => g.Select(c => new AcreditacionResumenDto(c.Id, c.NombrePlataforma, c.Estado)).ToList());
    }
}
