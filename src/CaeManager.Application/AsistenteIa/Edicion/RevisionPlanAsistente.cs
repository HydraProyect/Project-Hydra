using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Edicion;

/// <summary>Qué órdenes del catálogo sabe ejecutar Vega desde el plan.</summary>
public static class EjecucionPlanAsistente
{
    public const string TextoNoLaHaceVega =
        "Vega todavía no hace esta gestión: hazla desde su pantalla.";

    /// <summary>
    /// Las operaciones que <see cref="EjecutarPlanTareaAsistenteCommandHandler"/> sabe
    /// construir. Una orden se ejecuta desde el plan solo si TODAS sus operaciones están
    /// aquí: las demás (reclamar documentación, que manda correo a un tercero, o la
    /// consulta de estado) se hacen desde su pantalla.
    /// </summary>
    public static readonly IReadOnlySet<Type> OperacionesConocidas = new HashSet<Type>
    {
        typeof(CrearTrabajadorCommand),
        typeof(CrearAsignacionCommand),
        typeof(CrearVisitaCommand),
        typeof(CrearCentroCommand),
        typeof(CrearClienteCommand),
    };

    public static bool SabeEjecutar(string ordenId) =>
        CatalogoOrdenesAsistente.PorId(ordenId) is { Ejecutable: true, Modo: ModoDeEjecucion.Secuencia } orden
        && orden.Ejecucion.Count > 0
        && orden.Ejecucion.All(p => OperacionesConocidas.Contains(p.Operacion));
}

/// <summary>
/// Revisa los datos de un plan tal como los completó el Gestor CAE y reconstruye el
/// <see cref="PlanPropuestoDto"/>, <b>solo en el Tenant efectivo</b> de la sesión. Lo usan
/// <see cref="CompletarPlanTareaAsistenteCommand"/> (al editar) y
/// <see cref="EjecutarPlanTareaAsistenteCommand"/> (otra vez, justo antes de ejecutar).
/// <para>
/// Falla cerrado: un valor de selección que no esté entre las opciones de la cartera
/// (<see cref="ObtenerOpcionesPlanAsistenteQuery"/>) no se guarda ni se ejecuta, aunque
/// lo traiga el cliente. No sustituye a la autorización de los Commands: es lo que
/// evita que el plan enseñe como listo algo que el Command va a rechazar.
/// </para>
/// </summary>
public sealed class RevisionPlanAsistente(IMediator mediator, ITrabajadorRepository trabajadores)
{
    public const string CodigoFueraDeCartera = "AsistenteIa.ValorFueraDeCartera";
    public const string CodigoSinCartera = "AsistenteIa.TenantFueraDeCartera";

    public async Task<Result<(PlanPropuestoDto Plan, OpcionesPlanAsistenteDto Opciones)>> RevisarAsync(
        string ordenId, int confianzaOrden, IReadOnlyList<ValorDatoPlanDto> valores, CancellationToken cancellationToken)
    {
        if (CatalogoOrdenesAsistente.PorId(ordenId) is not { } orden)
            return Fallo(Error.Crear("AsistenteIa.OrdenDesconocida", "La orden no está en el catálogo del asistente."));

        var opciones = await mediator.Send(new ObtenerOpcionesPlanAsistenteQuery(ordenId), cancellationToken);
        if (opciones.Tenant is not { } tenant)
            return Fallo(Error.Crear(CodigoSinCartera,
                "La Empresa gestionada que tienes abierta no es de tu cartera: elige una en «Trabajando en»."));

        var porCampo = new Dictionary<string, ValorDatoPlanDto>(StringComparer.Ordinal);
        foreach (var valor in valores)
        {
            if (valor is null || string.IsNullOrWhiteSpace(valor.Campo))
                continue;
            porCampo[valor.Campo] = valor;
        }

        var avisos = new List<AvisoPasoTareaAsistente>();
        var datos = new List<DatoPropuestoDto>();
        foreach (var campo in orden.Campos.Where(c => c.Nombre != ObtenerCandidatosAsistenteQueryHandler.CampoTenant))
        {
            porCampo.TryGetValue(campo.Nombre, out var valor);
            var dato = Revisar(orden.Id, campo, valor, opciones, tenant, avisos);
            if (dato.EsFallido)
                return Fallo(dato.Error);
            datos.Add(dato.Valor);
        }

        await AvisosCruzadosAsync(datos, opciones, avisos, cancellationToken);

        var destino = new TenantDestinoDto(SituacionTenantDestino.Unico, tenant, [tenant], string.Empty);
        var ejecutable = orden.Ejecutable && EjecucionPlanAsistente.SabeEjecutar(orden.Id);
        var limitacion = !ejecutable
            ? EjecucionPlanAsistente.TextoNoLaHaceVega
            : string.IsNullOrWhiteSpace(orden.Limitacion) ? null : orden.Limitacion;
        var plan = new PlanPropuestoDto(
            SituacionPlan.Propuesto, orden.Id, confianzaOrden, datos, destino, ejecutable, limitacion,
            DestinoEsLaPantalla: true, Avisos: avisos);
        return Result.Exito((plan, opciones));
    }

    private static Result<(PlanPropuestoDto, OpcionesPlanAsistenteDto)> Fallo(Error error) =>
        Result.Fallo<(PlanPropuestoDto, OpcionesPlanAsistenteDto)>(error);

    private static Result<DatoPropuestoDto> Revisar(
        string ordenId, CampoDeOrden campo, ValorDatoPlanDto? valor, OpcionesPlanAsistenteDto opciones,
        TenantDeCarteraDto tenant, List<AvisoPasoTareaAsistente> avisos)
    {
        DatoPropuestoDto Vacio() => new(campo.Nombre, campo.Descripcion, campo.Obligatorio, null, null, null, 0, null);
        if (valor is null)
            return Vacio();

        switch (TiposDatoPlan.De(ordenId, campo))
        {
            case TipoDatoPlan.Seleccion:
                if (valor.Id is not { } id)
                    return Vacio();
                if (opciones.Buscar(campo.Nombre, id) is not { } opcion)
                    return FueraDeCartera(campo);
                return new DatoPropuestoDto(campo.Nombre, campo.Descripcion, campo.Obligatorio, id, opcion.Etiqueta,
                    tenant.TenantId, 100, null, new ValorDatoPlanDto(campo.Nombre, Id: id));

            case TipoDatoPlan.SeleccionMultiple:
                var ids = (valor.Ids ?? []).Distinct().ToList();
                if (ids.Count == 0)
                    return Vacio();
                if (ids.Count > TareaAsistenteLimites.MaximoSeleccionMultiple)
                    return Result.Fallo<DatoPropuestoDto>(Error.Crear("AsistenteIa.DemasiadosValores",
                        $"Elige como mucho {TareaAsistenteLimites.MaximoSeleccionMultiple} personas."));
                var elegidas = ids.Select(i => opciones.Buscar(campo.Nombre, i)).ToList();
                if (elegidas.Any(o => o is null))
                    return FueraDeCartera(campo);
                return new DatoPropuestoDto(campo.Nombre, campo.Descripcion, campo.Obligatorio, null,
                    string.Join(", ", elegidas.Select(o => o!.Etiqueta)), tenant.TenantId, 100, null,
                    new ValorDatoPlanDto(campo.Nombre, Ids: ids));

            case TipoDatoPlan.Fecha:
                return valor.Fecha is { } fecha
                    ? Con(campo, new ValorDatoPlanDto(campo.Nombre, Fecha: fecha), fecha.ToString("dd/MM/yyyy"))
                    : Vacio();

            case TipoDatoPlan.SiNo:
                return valor.SiNo is { } siNo
                    ? Con(campo, new ValorDatoPlanDto(campo.Nombre, SiNo: siNo), siNo ? "Sí" : "No")
                    : Vacio();

            case TipoDatoPlan.Texto:
                var texto = valor.Texto?.Trim();
                if (string.IsNullOrEmpty(texto))
                    return Vacio();
                var maximo = LongitudMaxima(campo.Nombre);
                if (texto.Length > maximo)
                    return Result.Fallo<DatoPropuestoDto>(Error.Crear("AsistenteIa.TextoDemasiadoLargo",
                        $"«{campo.Descripcion}» no puede pasar de {maximo} caracteres."));
                return Con(campo, new ValorDatoPlanDto(campo.Nombre, Texto: texto), texto);

            case TipoDatoPlan.TrabajadorNuevo:
                if (valor.Trabajador is not { } persona)
                    return Vacio();
                var limpio = new TrabajadorNuevoDto(
                    persona.Nombre?.Trim() ?? string.Empty,
                    persona.Apellidos?.Trim() ?? string.Empty,
                    persona.Documento?.Trim() ?? string.Empty);
                if (limpio.Nombre.Length > Trabajador.LongitudMaximaNombre
                    || limpio.Apellidos.Length > Trabajador.LongitudMaximaApellidos
                    || limpio.Documento.Length > TiposDatoPlan.LongitudMaximaTexto)
                    return Result.Fallo<DatoPropuestoDto>(Error.Crear("AsistenteIa.TextoDemasiadoLargo",
                        "El nombre, los apellidos o el documento son demasiado largos."));
                var etiqueta = $"{limpio.Nombre} {limpio.Apellidos}".Trim();
                return Con(campo, new ValorDatoPlanDto(campo.Nombre, Trabajador: limpio),
                    string.IsNullOrEmpty(limpio.Documento) ? etiqueta : $"{etiqueta} · {limpio.Documento}");

            default:
                return Vacio();
        }
    }

    /// <summary>
    /// El límite del Command que recibirá el texto, no uno propio: un plan que se puede
    /// confirmar no puede acabar rechazado por el validador del Command al ejecutar.
    /// </summary>
    internal static int LongitudMaxima(string campo) => Math.Min(TiposDatoPlan.LongitudMaximaTexto, campo switch
    {
        TiposDatoPlan.CampoNombre => Domain.Centros.Centro.LongitudMaximaNombre,
        TiposDatoPlan.CampoDireccion => Domain.Centros.Centro.LongitudMaximaDireccion,
        TiposDatoPlan.CampoRazonSocial => Domain.Empresas.Empresa.LongitudMaximaRazonSocial,
        TiposDatoPlan.CampoMotivo => Domain.Visitas.Visita.LongitudMaximaNotas,
        _ => TiposDatoPlan.LongitudMaximaTexto,
    });

    private static Result<DatoPropuestoDto> Con(CampoDeOrden campo, ValorDatoPlanDto valor, string nombre) =>
        new DatoPropuestoDto(campo.Nombre, campo.Descripcion, campo.Obligatorio, null, nombre, null, 100, null, valor);

    private static Result<DatoPropuestoDto> FueraDeCartera(CampoDeOrden campo) =>
        Result.Fallo<DatoPropuestoDto>(Error.Crear(CodigoFueraDeCartera,
            $"El valor elegido para «{campo.Descripcion}» no es de tu cartera en esta Empresa gestionada."));

    /// <summary>Las comprobaciones entre datos: lo que el Command rechazaría, dicho antes de confirmar.</summary>
    private async Task AvisosCruzadosAsync(
        IReadOnlyList<DatoPropuestoDto> datos, OpcionesPlanAsistenteDto opciones,
        List<AvisoPasoTareaAsistente> avisos, CancellationToken cancellationToken)
    {
        var porCampo = datos.ToDictionary(d => d.Campo, StringComparer.Ordinal);

        if (porCampo.TryGetValue(ObtenerCandidatosAsistenteQueryHandler.CampoCentro, out var centro) && centro.CandidatoId is { } centroId
            && porCampo.TryGetValue(ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial, out var cliente)
            && cliente.CandidatoId is { } clienteEmpresarialId
            && opciones.Buscar(ObtenerCandidatosAsistenteQueryHandler.CampoCentro, centroId) is { GrupoId: { } grupo }
            && grupo != clienteEmpresarialId)
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante,
                "El Centro elegido no es de ese Cliente empresarial.", ObtenerCandidatosAsistenteQueryHandler.CampoCentro));

        if (porCampo.TryGetValue(TiposDatoPlan.CampoTrabajador, out var persona) && persona.Valor?.Trabajador is { } nuevo
            && !string.IsNullOrEmpty(nuevo.Documento))
        {
            var documento = CriterioDocumentoIdentidadTrabajador.Clasificar(nuevo.Documento);
            if (!documento.EsValido)
                avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, documento.Error ?? "El documento de identidad no es válido.",
                    TiposDatoPlan.CampoTrabajador));
            else
            {
                if (documento.SeGuardaComoPasaporte)
                    avisos.Add(new(GravedadAvisoPasoTareaAsistente.Advertencia, CriterioDocumentoIdentidadTrabajador.AvisoPasaporte + ".",
                        TiposDatoPlan.CampoTrabajador));
                if (await trabajadores.ExisteConDniAsync(documento.Normalizado!, cancellationToken: cancellationToken))
                    avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante,
                        "Ya hay un Trabajador con ese documento en esta Empresa gestionada: búscalo en Trabajadores en vez de darlo de alta otra vez.",
                        TiposDatoPlan.CampoTrabajador));
            }
        }

        var inicio = porCampo.GetValueOrDefault(TiposDatoPlan.CampoFechaInicio)?.Valor?.Fecha;
        var fin = porCampo.GetValueOrDefault(TiposDatoPlan.CampoFechaFin)?.Valor?.Fecha;
        if (inicio is { } i && fin is { } f && f < i)
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante,
                "La fecha de fin no puede ser anterior a la de inicio.", TiposDatoPlan.CampoFechaFin));
        if (inicio is { } desde && desde < DiaDeNegocio.Hoy())
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Advertencia,
                "La fecha de inicio ya ha pasado.", TiposDatoPlan.CampoFechaInicio));

        if (porCampo.GetValueOrDefault(TiposDatoPlan.CampoCif)?.Valor?.Texto is { } cif
            && !ValidadorIdentificacion.EsIdentificacionFiscalValida(cif))
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante,
                "La identificación fiscal no es válida.", TiposDatoPlan.CampoCif));
    }
}

public static class TareaAsistenteLimites
{
    /// <summary>Máximo de personas en una Visita creada desde el plan.</summary>
    public const int MaximoSeleccionMultiple = 50;
}
